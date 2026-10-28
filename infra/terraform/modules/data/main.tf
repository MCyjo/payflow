variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "sku_name" { type = string }
variable "tags" { type = map(string) }

locals {
  admin_login = "payflowadmin"
  databases   = ["orders", "payments"] # one database per service: services share a server, never a schema
}

# No special characters: the value ends up inside a connection string.
resource "random_password" "admin" {
  length  = 32
  special = false
}

resource "azurerm_postgresql_flexible_server" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location

  version                = "17"
  sku_name               = var.sku_name
  storage_mb             = 32768
  administrator_login    = local.admin_login
  administrator_password = random_password.admin.result

  backup_retention_days         = 7
  geo_redundant_backup_enabled  = false
  public_network_access_enabled = true # demo: see the README for the private-endpoint variant

  tags = var.tags

  lifecycle {
    # Azure picks an availability zone on create; do not fight it afterwards.
    ignore_changes = [zone, high_availability[0].standby_availability_zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "this" {
  for_each = toset(local.databases)

  name      = each.key
  server_id = azurerm_postgresql_flexible_server.this.id
  collation = "en_US.utf8"
  charset   = "UTF8"
}

# 0.0.0.0 is Azure's special "allow Azure services" rule: AKS and Functions reach the server, the internet does not
# (it is still reachable from any Azure tenant, which is why the password is long and random).
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

locals {
  connection_strings = {
    for db in local.databases : db => join(";", [
      "Host=${azurerm_postgresql_flexible_server.this.fqdn}",
      "Port=5432",
      "Database=${db}",
      "Username=${local.admin_login}",
      "Password=${random_password.admin.result}",
      "SSL Mode=Require",
      "Trust Server Certificate=true",
    ])
  }
}

output "fqdn" { value = azurerm_postgresql_flexible_server.this.fqdn }
output "connection_strings" {
  value     = local.connection_strings
  sensitive = true
}
