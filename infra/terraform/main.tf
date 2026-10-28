locals {
  base_name = "${var.project}-${var.environment}"
  tags = {
    project     = var.project
    environment = var.environment
    managed_by  = "terraform"
  }
}

# Globally unique names (registry, vault, storage, ...) need a suffix that survives re-applies.
resource "random_string" "suffix" {
  length  = 4
  upper   = false
  special = false
}

resource "azurerm_resource_group" "this" {
  name     = "rg-${local.base_name}"
  location = var.location
  tags     = local.tags
}

module "observability" {
  source = "./modules/observability"

  name_suffix         = local.base_name
  resource_group_name = azurerm_resource_group.this.name
  location            = var.location
  daily_quota_gb      = var.log_daily_quota_gb
  tags                = local.tags
}

module "messaging" {
  source = "./modules/messaging"

  name                = "sb-${local.base_name}-${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.this.name
  location            = var.location
  tags                = local.tags
}

module "data" {
  source = "./modules/data"

  name                = "psql-${local.base_name}-${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.this.name
  location            = var.location
  sku_name            = var.postgres_sku
  tags                = local.tags
}

resource "azurerm_container_registry" "this" {
  name                = "acr${var.project}${var.environment}${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.this.name
  location            = var.location
  sku                 = "Basic"
  admin_enabled       = false # pushes use Entra ID (the CI identity), pulls use the kubelet identity
  tags                = local.tags
}

module "aks" {
  source = "./modules/aks"
  count  = var.deploy_aks ? 1 : 0

  name                = "aks-${local.base_name}"
  resource_group_name = azurerm_resource_group.this.name
  location            = var.location
  node_vm_size        = var.aks_node_vm_size
  acr_id              = azurerm_container_registry.this.id
  tags                = local.tags
}

resource "random_password" "webhook_secret" {
  length  = 40
  special = false
}

module "security" {
  source = "./modules/security"

  name                     = "kv-${local.base_name}-${random_string.suffix.result}"
  resource_group_name      = azurerm_resource_group.this.name
  location                 = var.location
  tags                     = local.tags
  service_bus_namespace_id = module.messaging.namespace_id
  k8s_namespace            = var.k8s_namespace
  deploy_aks               = var.deploy_aks
  oidc_issuer_url          = var.deploy_aks ? module.aks[0].oidc_issuer_url : null

  # What the pods read through the Key Vault CSI driver (names are referenced by deploy/kubernetes).
  secrets = {
    "orders-connection-string"      = module.data.connection_strings["orders"]
    "payments-connection-string"    = module.data.connection_strings["payments"]
    "webhook-secret"                = random_password.webhook_secret.result
    "appinsights-connection-string" = module.observability.connection_string
  }
}

module "functions" {
  source = "./modules/functions"

  name                           = "func-${local.base_name}-${random_string.suffix.result}"
  storage_account_name           = "st${var.project}${var.environment}${random_string.suffix.result}"
  resource_group_name            = azurerm_resource_group.this.name
  location                       = var.location
  service_bus_namespace_id       = module.messaging.namespace_id
  service_bus_fqdn               = module.messaging.fully_qualified_namespace
  app_insights_connection_string = module.observability.connection_string
  tags                           = local.tags
}
