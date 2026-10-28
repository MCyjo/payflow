variable "name" { type = string }
variable "storage_account_name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "service_bus_namespace_id" { type = string }
variable "service_bus_fqdn" { type = string }
variable "app_insights_connection_string" {
  type      = string
  sensitive = true
}
variable "tags" { type = map(string) }

resource "azurerm_storage_account" "this" {
  name                            = var.storage_account_name
  resource_group_name             = var.resource_group_name
  location                        = var.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  tags                            = var.tags
}

# Consumption plan: ~1M executions a month are free, and it scales to zero.
resource "azurerm_service_plan" "this" {
  name                = "asp-${var.name}"
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "Y1"
  tags                = var.tags
}

resource "azurerm_linux_function_app" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = azurerm_service_plan.this.id
  https_only          = true

  # The Functions host itself still needs a storage key on the consumption plan.
  storage_account_name       = azurerm_storage_account.this.name
  storage_account_access_key = azurerm_storage_account.this.primary_access_key

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_insights_connection_string = var.app_insights_connection_string
    minimum_tls_version                    = "1.2"
    ftps_state                             = "Disabled"

    application_stack {
      dotnet_version              = "10.0"
      use_dotnet_isolated_runtime = true
    }
  }

  app_settings = {
    # Identity-based Service Bus trigger: the binding "ServiceBus" resolves to this namespace, no connection string.
    "ServiceBus__fullyQualifiedNamespace" = var.service_bus_fqdn
  }

  tags = var.tags
}

# The trigger needs to receive from the subscriptions it listens to.
resource "azurerm_role_assignment" "receiver" {
  scope                = var.service_bus_namespace_id
  role_definition_name = "Azure Service Bus Data Receiver"
  principal_id         = azurerm_linux_function_app.this.identity[0].principal_id
}

output "name" { value = azurerm_linux_function_app.this.name }
