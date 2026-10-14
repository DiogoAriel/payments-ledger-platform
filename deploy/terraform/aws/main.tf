# ECS Fargate service behind an ALB, PostgreSQL on RDS, secrets in Secrets Manager.
# Everything the task needs at runtime comes from Secrets Manager or environment variables:
# the image is immutable and identical across environments.

locals {
  name = "payments-ledger-${var.environment}"
}

# --- Encryption keys -------------------------------------------------------------------------

# Customer-managed key for data at rest: database storage, Performance Insights and secrets.
# The default key policy (account root) applies; access is granted through IAM.
resource "aws_kms_key" "data" {
  description             = "${local.name} data encryption"
  enable_key_rotation     = true
  deletion_window_in_days = 30
  policy                  = data.aws_iam_policy_document.data_key.json
}

# Explicit key policy: the account administers the key; RDS and Secrets Manager use it through
# grants created on behalf of the task role (kms:ViaService keeps it to those two services).
data "aws_iam_policy_document" "data_key" {
  # checkov:skip=CKV_AWS_356: a KMS key policy must name the key itself as "*"
  # checkov:skip=CKV_AWS_109: key administration statement is intrinsic to a key policy
  # checkov:skip=CKV_AWS_111: same as above
  statement {
    sid       = "AccountAdministration"
    actions   = ["kms:*"]
    resources = ["*"]
    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:root"]
    }
  }

  statement {
    sid = "ServiceUse"
    actions = [
      "kms:Encrypt",
      "kms:Decrypt",
      "kms:ReEncrypt*",
      "kms:GenerateDataKey*",
      "kms:DescribeKey",
      "kms:CreateGrant",
    ]
    resources = ["*"]
    principals {
      type        = "AWS"
      identifiers = [aws_iam_role.task_execution.arn, aws_iam_role.task.arn]
    }
    condition {
      test     = "StringEquals"
      variable = "kms:ViaService"
      values   = ["rds.${var.region}.amazonaws.com", "secretsmanager.${var.region}.amazonaws.com"]
    }
  }
}

# --- Secrets ---------------------------------------------------------------------------------

resource "random_password" "db" {
  length  = 32
  special = false
}

resource "aws_secretsmanager_secret" "db" {
  # checkov:skip=CKV2_AWS_57: the database password is rotated by the platform's rotation job, which also updates this connection string; the Lambda-based rotation is out of scope for the reference project.
  name                    = "${local.name}/database"
  recovery_window_in_days = 7
  kms_key_id              = aws_kms_key.data.arn
}

resource "aws_secretsmanager_secret_version" "db" {
  secret_id = aws_secretsmanager_secret.db.id
  secret_string = jsonencode({
    ConnectionStrings__Ledger = "Host=${aws_db_instance.ledger.address};Port=5432;Database=ledger;Username=ledger;Password=${random_password.db.result};SSL Mode=Require"
  })
}

# Webhook secrets are rotated by the provider onboarding process, outside Terraform: the secret
# exists here so the task role can read it, but its value is never in state or in code.
resource "aws_secretsmanager_secret" "webhooks" {
  # checkov:skip=CKV2_AWS_57: webhook secrets are rotated with each provider through its onboarding process, not by an automatic schedule.
  name                    = "${local.name}/webhooks"
  recovery_window_in_days = 7
  kms_key_id              = aws_kms_key.data.arn
}

# --- Database --------------------------------------------------------------------------------

resource "aws_db_subnet_group" "ledger" {
  name       = local.name
  subnet_ids = var.private_subnet_ids
}

resource "aws_security_group" "db" {
  name        = "${local.name}-db"
  description = "PostgreSQL: accepts connections from the API tasks only"
  vpc_id      = var.vpc_id

  ingress {
    description     = "PostgreSQL from the API security group"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.api.id]
  }
}

# Enhanced monitoring publishes OS-level metrics to CloudWatch through this role.
resource "aws_iam_role" "rds_monitoring" {
  name = "${local.name}-rds-monitoring"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Action    = "sts:AssumeRole"
      Principal = { Service = "monitoring.rds.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "rds_monitoring" {
  role       = aws_iam_role.rds_monitoring.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonRDSEnhancedMonitoringRole"
}

# Query logging: DDL statements and anything slower than one second go to the PostgreSQL log,
# which is exported to CloudWatch below. Full statement logging would leak amounts and ids.
resource "aws_db_parameter_group" "ledger" {
  name   = local.name
  family = "postgres16"

  parameter {
    name  = "log_statement"
    value = "ddl"
  }

  parameter {
    name  = "log_min_duration_statement"
    value = "1000"
  }

  # Encryption in transit is mandatory; the connection string uses SSL Mode=Require.
  parameter {
    name  = "rds.force_ssl"
    value = "1"
  }
}

resource "aws_db_instance" "ledger" {
  identifier                      = local.name
  engine                          = "postgres"
  engine_version                  = "16"
  parameter_group_name            = aws_db_parameter_group.ledger.name
  instance_class                  = var.db_instance_class
  allocated_storage               = 50
  storage_encrypted               = true
  kms_key_id                      = aws_kms_key.data.arn
  db_name                         = "ledger"
  username                        = "ledger"
  password                        = random_password.db.result
  db_subnet_group_name            = aws_db_subnet_group.ledger.name
  vpc_security_group_ids          = [aws_security_group.db.id]
  multi_az                        = true
  copy_tags_to_snapshot           = true
  backup_retention_period         = 7
  deletion_protection             = true
  skip_final_snapshot             = false
  final_snapshot_identifier       = "${local.name}-final"
  auto_minor_version_upgrade      = true
  monitoring_interval             = 60
  monitoring_role_arn             = aws_iam_role.rds_monitoring.arn
  performance_insights_enabled    = true
  performance_insights_kms_key_id = aws_kms_key.data.arn
  enabled_cloudwatch_logs_exports = ["postgresql"]

  # Application access uses IAM tokens where the runtime supports it; the password remains for
  # tooling and migrations. Enabling it costs nothing and keeps the door open.
  iam_database_authentication_enabled = true
}

# --- Compute ---------------------------------------------------------------------------------

resource "aws_ecs_cluster" "ledger" {
  name = local.name

  setting {
    name  = "containerInsights"
    value = "enabled"
  }
}

# One customer-managed key for every log group of the service; CloudWatch Logs must be allowed
# to use it, scoped to log groups in this account and region.
resource "aws_kms_key" "logs" {
  description             = "${local.name} log encryption"
  enable_key_rotation     = true
  deletion_window_in_days = 30
  policy                  = data.aws_iam_policy_document.logs_key.json
}

data "aws_caller_identity" "current" {}

data "aws_iam_policy_document" "logs_key" {
  # checkov:skip=CKV_AWS_356: a KMS key policy must name the key itself as "*"; access is scoped by the principals and the encryption-context condition below.
  statement {
    sid       = "AccountAdministration"
    actions   = ["kms:*"]
    resources = ["*"]
    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:root"]
    }
  }

  statement {
    sid = "CloudWatchLogs"
    actions = [
      "kms:Encrypt*",
      "kms:Decrypt*",
      "kms:ReEncrypt*",
      "kms:GenerateDataKey*",
      "kms:Describe*",
    ]
    resources = ["*"]
    principals {
      type        = "Service"
      identifiers = ["logs.${var.region}.amazonaws.com"]
    }
    condition {
      test     = "ArnLike"
      variable = "kms:EncryptionContext:aws:logs:arn"
      values   = ["arn:aws:logs:${var.region}:${data.aws_caller_identity.current.account_id}:log-group:*"]
    }
  }
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/ecs/${local.name}"
  retention_in_days = 365
  kms_key_id        = aws_kms_key.logs.arn
}

resource "aws_iam_role" "task_execution" {
  name               = "${local.name}-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
}

resource "aws_iam_role" "task" {
  name               = "${local.name}-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
}

data "aws_iam_policy_document" "ecs_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.task_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

# Least privilege: the execution role may read exactly the two secrets this service owns.
data "aws_iam_policy_document" "read_secrets" {
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_secretsmanager_secret.db.arn, aws_secretsmanager_secret.webhooks.arn]
  }
}

resource "aws_iam_role_policy" "read_secrets" {
  role   = aws_iam_role.task_execution.id
  policy = data.aws_iam_policy_document.read_secrets.json
}

resource "aws_security_group" "api" {
  name        = "${local.name}-api"
  description = "API tasks: HTTP from the load balancer, TLS and PostgreSQL out"
  vpc_id      = var.vpc_id

  ingress {
    description     = "HTTP from the load balancer security group"
    from_port       = 8080
    to_port         = 8080
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }

  # Egress is limited to what the task actually talks to: TLS for Secrets Manager, ECR, Kafka
  # and the OTLP collector here, and the database through a separate rule below (a separate
  # resource avoids a dependency cycle between the api and db security groups).
  # Narrow the TLS rule to VPC endpoints once they exist in the target VPC.
  #trivy:ignore:AVD-AWS-0104
  egress {
    description = "TLS to AWS APIs, ECR, Kafka and the OTLP collector"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_vpc_security_group_egress_rule" "api_to_db" {
  description                  = "PostgreSQL to the database security group"
  security_group_id            = aws_security_group.api.id
  referenced_security_group_id = aws_security_group.db.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

resource "aws_ecs_task_definition" "api" {
  family                   = local.name
  network_mode             = "awsvpc"
  requires_compatibilities = ["FARGATE"]
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.task_execution.arn
  task_role_arn            = aws_iam_role.task.arn

  container_definitions = jsonencode([
    {
      name         = "api"
      image        = var.image
      essential    = true
      portMappings = [{ containerPort = 8080, protocol = "tcp" }]
      environment = [
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "Database__Provider", value = "Postgres" },
        { name = "Database__AutoMigrate", value = "false" },
        { name = "Messaging__Kafka__BootstrapServers", value = var.kafka_bootstrap_servers },
        { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = var.otlp_endpoint },
      ]
      secrets = [
        { name = "ConnectionStrings__Ledger", valueFrom = "${aws_secretsmanager_secret.db.arn}:ConnectionStrings__Ledger::" },
      ]
      readonlyRootFilesystem = true
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.api.name
          "awslogs-region"        = var.region
          "awslogs-stream-prefix" = "api"
        }
      }
    }
  ])
}

resource "aws_ecs_service" "api" {
  name            = local.name
  cluster         = aws_ecs_cluster.ledger.id
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets         = var.private_subnet_ids
    security_groups = [aws_security_group.api.id]
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.api.arn
    container_name   = "api"
    container_port   = 8080
  }

  depends_on = [aws_lb_listener.https]
}

# --- Load balancer ---------------------------------------------------------------------------

resource "aws_security_group" "alb" {
  name        = "${local.name}-alb"
  description = "Public load balancer: HTTPS in, API tasks out"
  vpc_id      = var.vpc_id

  ingress {
    description = "HTTPS from the internet"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

# The load balancer may only talk to the API tasks. Declared as a separate rule so the two
# security groups can reference each other without a cycle.
resource "aws_vpc_security_group_egress_rule" "alb_to_api" {
  description                  = "HTTP to the API security group"
  security_group_id            = aws_security_group.alb.id
  referenced_security_group_id = aws_security_group.api.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
}

# Public by design: this is the internet-facing edge of the API, protected by TLS 1.3, WAF and
# the security group above.
#trivy:ignore:AVD-AWS-0053
resource "aws_lb" "api" {
  name                       = local.name
  load_balancer_type         = "application"
  subnets                    = var.public_subnet_ids
  security_groups            = [aws_security_group.alb.id]
  drop_invalid_header_fields = true
  enable_deletion_protection = true
}

# Public-facing ALB behind AWS WAF with the managed common rule set (OWASP-style protections).
resource "aws_wafv2_web_acl" "api" {
  name  = local.name
  scope = "REGIONAL"

  default_action {
    allow {}
  }

  # Known bad inputs first (includes the Log4j JNDI patterns), then the common rule set.
  rule {
    name     = "aws-managed-known-bad-inputs"
    priority = 1

    override_action {
      none {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesKnownBadInputsRuleSet"
        vendor_name = "AWS"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${local.name}-known-bad-inputs"
      sampled_requests_enabled   = true
    }
  }

  rule {
    name     = "aws-managed-common"
    priority = 2

    override_action {
      none {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesCommonRuleSet"
        vendor_name = "AWS"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${local.name}-common"
      sampled_requests_enabled   = true
    }
  }

  # Anonymous sources (VPNs, proxies, Tor exit nodes) are a poor fit for a payments API.
  rule {
    name     = "aws-managed-anonymous-ip"
    priority = 3

    override_action {
      none {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesAnonymousIpList"
        vendor_name = "AWS"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${local.name}-anonymous-ip"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = local.name
    sampled_requests_enabled   = true
  }
}

resource "aws_wafv2_web_acl_association" "api" {
  resource_arn = aws_lb.api.arn
  web_acl_arn  = aws_wafv2_web_acl.api.arn
}

# WAF logs go to CloudWatch; the log group name must start with "aws-waf-logs-".
resource "aws_cloudwatch_log_group" "waf" {
  name              = "aws-waf-logs-${local.name}"
  retention_in_days = 365
  kms_key_id        = aws_kms_key.logs.arn
}

resource "aws_wafv2_web_acl_logging_configuration" "api" {
  resource_arn            = aws_wafv2_web_acl.api.arn
  log_destination_configs = [aws_cloudwatch_log_group.waf.arn]
}

resource "aws_lb_target_group" "api" {
  name        = local.name
  port        = 8080
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = var.vpc_id

  health_check {
    path                = "/health/ready"
    healthy_threshold   = 2
    unhealthy_threshold = 3
    interval            = 15
  }
}

resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.api.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = data.aws_acm_certificate.api.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}

data "aws_acm_certificate" "api" {
  domain      = "ledger.${var.environment}.example.com"
  statuses    = ["ISSUED"]
  most_recent = true
}
