variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "node_vm_size" { type = string }
variable "acr_id" { type = string }
variable "tags" { type = map(string) }

resource "azurerm_kubernetes_cluster" "this" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  dns_prefix          = var.name
  sku_tier            = "Free" # no SLA, no control-plane charge: fine for a demo

  # Workload identity: pods exchange their service account token for an Entra ID token (see the security module).
  oidc_issuer_enabled       = true
  workload_identity_enabled = true

  default_node_pool {
    name                        = "system"
    vm_size                     = var.node_vm_size
    node_count                  = 1
    os_disk_size_gb             = 32
    temporary_name_for_rotation = "tmpsystem"

    upgrade_settings {
      max_surge = "10%"
    }
  }

  identity {
    type = "SystemAssigned"
  }

  network_profile {
    network_plugin      = "azure"
    network_plugin_mode = "overlay" # pods get overlay IPs, so the cluster does not eat VNet address space
  }

  # Mounts Key Vault secrets into pods (SecretProviderClass) using the pod's workload identity.
  key_vault_secrets_provider {
    secret_rotation_enabled = false
  }

  tags = var.tags
}

# Lets the nodes pull images from the registry without any pull secret.
resource "azurerm_role_assignment" "acr_pull" {
  scope                            = var.acr_id
  role_definition_name             = "AcrPull"
  principal_id                     = azurerm_kubernetes_cluster.this.kubelet_identity[0].object_id
  skip_service_principal_aad_check = true
}

output "name" { value = azurerm_kubernetes_cluster.this.name }
output "oidc_issuer_url" { value = azurerm_kubernetes_cluster.this.oidc_issuer_url }
