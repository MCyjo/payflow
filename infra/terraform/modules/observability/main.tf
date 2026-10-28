variable "name_suffix" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "daily_quota_gb" { type = number }
variable "tags" { type = map(string) }

resource "azurerm_log_analytics_workspace" "this" {
  name                = "log-${var.name_suffix}"
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = var.daily_quota_gb # cost guard rail: ingestion stops at the cap
  tags                = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${var.name_suffix}"
  location            = var.location
  resource_group_name = var.resource_group_name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"
  tags                = var.tags
}

output "connection_string" {
  value     = azurerm_application_insights.this.connection_string
  sensitive = true
}
output "workspace_id" { value = azurerm_log_analytics_workspace.this.id }
