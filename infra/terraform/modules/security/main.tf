variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "tags" { type = map(string) }
variable "service_bus_namespace_id" { type = string }
variable "k8s_namespace" { type = string }

# Federation needs the cluster's OIDC issuer. `deploy_aks` is known at plan time, the issuer URL is not,
# so it (and not the URL) drives `count`.
variable "deploy_aks" { type = bool }
variable "oidc_issuer_url" {
  type    = string
  default = null
}

variable "secrets" {
  description = "Secrets to store in Key Vault (name => value)."
  type        = map(string)
  sensitive   = true
}

data "azurerm_client_config" "current" {}

locals {
  # One identity per workload: least privilege, and an audit trail that says which service did what.
  workloads = {
    "order-service"      = { send = true, receive = true }
    "payment-service"    = { send = true, receive = true }
    "provider-simulator" = { send = false, receive = false }
  }
  service_bus_roles = merge(
    { for w, p in local.workloads : "${w}-sender" => { workload = w, role = "Azure Service Bus Data Sender" } if p.send },
    { for w, p in local.workloads : "${w}-receiver" => { workload = w, role = "Azure Service Bus Data Receiver" } if p.receive },
  )
}

resource "azurerm_key_vault" "this" {
  name                       = var.name
  location                   = var.location
  resource_group_name        = var.resource_group_name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true # access via Azure roles, no per-vault access policies
  soft_delete_retention_days = 7
  purge_protection_enabled   = false # dev only: enable it in production
  tags                       = var.tags
}

# The identity running Terraform needs to write secrets.
resource "azurerm_role_assignment" "deployer_secrets_officer" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Role assignments take a while to propagate; writing a secret straight away fails with 403.
resource "time_sleep" "rbac_propagation" {
  depends_on      = [azurerm_role_assignment.deployer_secrets_officer]
  create_duration = "60s"
}

resource "azurerm_key_vault_secret" "this" {
  for_each = nonsensitive(toset(keys(var.secrets)))

  name         = each.key
  value        = var.secrets[each.key]
  key_vault_id = azurerm_key_vault.this.id

  depends_on = [time_sleep.rbac_propagation]
}

resource "azurerm_user_assigned_identity" "workload" {
  for_each = local.workloads

  name                = "id-${each.key}-${var.name}"
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

# The pod's Kubernetes service account is trusted to act as the identity: no secret, no certificate to rotate.
resource "azurerm_federated_identity_credential" "workload" {
  for_each = var.deploy_aks ? local.workloads : {}

  name                = "aks-${each.key}"
  resource_group_name = var.resource_group_name
  parent_id           = azurerm_user_assigned_identity.workload[each.key].id
  issuer              = var.oidc_issuer_url
  subject             = "system:serviceaccount:${var.k8s_namespace}:${each.key}"
  audience            = ["api://AzureADTokenExchange"]
}

resource "azurerm_role_assignment" "workload_secrets_user" {
  for_each = local.workloads

  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets User" # read-only
  principal_id         = azurerm_user_assigned_identity.workload[each.key].principal_id
}

resource "azurerm_role_assignment" "workload_service_bus" {
  for_each = local.service_bus_roles

  scope                = var.service_bus_namespace_id
  role_definition_name = each.value.role
  principal_id         = azurerm_user_assigned_identity.workload[each.value.workload].principal_id
}

output "key_vault_name" { value = azurerm_key_vault.this.name }
output "key_vault_id" { value = azurerm_key_vault.this.id }
output "tenant_id" { value = data.azurerm_client_config.current.tenant_id }
output "workload_client_ids" {
  value = { for w, id in azurerm_user_assigned_identity.workload : w => id.client_id }
}
