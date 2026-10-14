terraform {
  required_version = ">= 1.6"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.68"
    }
  }

  # Remote state is configured per environment with `terraform init -backend-config=...`.
  backend "s3" {}
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project     = "payments-ledger"
      Environment = var.environment
      ManagedBy   = "terraform"
    }
  }
}
