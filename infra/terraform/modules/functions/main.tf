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

# Flex Consumption keeps the deployed package in a blob container.
resource "azurerm_storage_container" "deployments" {
  name                  = "app-package"
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "private"
}

# Flex Consumption (FC1): pay per execution, scales to zero. .NET 10 cannot run on the Linux Consumption plan (Y1), and
# Microsoft is retiring that plan anyway, so Flex is the plan to use for new apps.
resource "azurerm_service_plan" "this" {
  name                = "asp-${var.name}-fc1" # new name on purpose: Y1 -> FC1 cannot be changed in place
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "FC1"
  tags                = var.tags
}

resource "azurerm_function_app_flex_consumption" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = azurerm_service_plan.this.id

  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${azurerm_storage_account.this.primary_blob_endpoint}${azurerm_storage_container.deployments.name}"
  storage_authentication_type = "StorageAccountConnectionString"
  storage_access_key          = azurerm_storage_account.this.primary_access_key

  runtime_name           = "dotnet-isolated"
  runtime_version        = "10.0"
  instance_memory_in_mb  = 2048
  maximum_instance_count = 40 # an upper bound, not a cost: instances exist only while there is work

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_insights_connection_string = var.app_insights_connection_string
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
  principal_id         = azurerm_function_app_flex_consumption.this.identity[0].principal_id
}

output "name" { value = azurerm_function_app_flex_consumption.this.name }
