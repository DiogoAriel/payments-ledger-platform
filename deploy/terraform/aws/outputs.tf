output "alb_dns_name" {
  description = "Public DNS name of the load balancer."
  value       = aws_lb.api.dns_name
}

output "db_endpoint" {
  description = "RDS endpoint (private)."
  value       = aws_db_instance.ledger.address
}

output "database_secret_arn" {
  description = "Secrets Manager ARN that holds the connection string."
  value       = aws_secretsmanager_secret.db.arn
}

output "webhooks_secret_arn" {
  description = "Secrets Manager ARN for provider webhook secrets."
  value       = aws_secretsmanager_secret.webhooks.arn
}
