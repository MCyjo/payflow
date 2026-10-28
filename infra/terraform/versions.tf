terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.12"
    }
  }

  # State lives in Azure Storage (created once by scripts/bootstrap-state.ps1).
  # Supply the details at init time: terraform init -backend-config=backend.hcl
  backend "azurerm" {}
}

provider "azurerm" {
  subscription_id = var.subscription_id

  # A new subscription has most resource providers unregistered, and the provider only registers a core set by
  # default. Without these the first apply fails with MissingSubscriptionRegistration.
  resource_provider_registrations = "core"
  resource_providers_to_register = [
    "Microsoft.ServiceBus",
    "Microsoft.DBforPostgreSQL",
    "Microsoft.KeyVault",
    "Microsoft.ContainerRegistry",
    "Microsoft.ContainerService",
    "Microsoft.OperationalInsights",
    "Microsoft.Insights",
    "Microsoft.Web",
    "Microsoft.ManagedIdentity",
  ]

  features {
    key_vault {
      # Lets `terraform destroy` followed by `apply` reuse names without waiting out the soft-delete period.
      purge_soft_delete_on_destroy    = true
      recover_soft_deleted_key_vaults = true
    }
    resource_group {
      # The demo resource group is disposable; do not block destroy on stray resources.
      prevent_deletion_if_contains_resources = false
    }
  }
}
