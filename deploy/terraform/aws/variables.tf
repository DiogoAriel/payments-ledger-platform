variable "region" {
  description = "AWS region."
  type        = string
  default     = "sa-east-1"
}

variable "environment" {
  description = "Environment name used in resource names and tags (dev, staging, prod)."
  type        = string
}

variable "vpc_id" {
  description = "VPC where the service and the database run."
  type        = string
}

variable "private_subnet_ids" {
  description = "Private subnets for ECS tasks and RDS."
  type        = list(string)
}

variable "public_subnet_ids" {
  description = "Public subnets for the application load balancer."
  type        = list(string)
}

variable "image" {
  description = "Container image for the API, including the tag."
  type        = string
}

variable "desired_count" {
  description = "Number of API tasks."
  type        = number
  default     = 2
}

variable "db_instance_class" {
  description = "RDS instance class."
  type        = string
  default     = "db.t4g.medium"
}

variable "kafka_bootstrap_servers" {
  description = "Kafka bootstrap servers (MSK or self-managed). Empty disables publishing."
  type        = string
  default     = ""
}

variable "otlp_endpoint" {
  description = "OTLP gRPC endpoint for traces and metrics."
  type        = string
  default     = ""
}
