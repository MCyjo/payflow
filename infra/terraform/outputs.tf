output "resource_group_name" {
  value = azurerm_resource_group.this.name
}

output "service_bus_fqdn" {
  description = "Value for ServiceBus__FullyQualifiedNamespace and SERVICEBUS_NAMESPACE (DLQ tool)."
  value       = module.messaging.fully_qualified_namespace
}

output "acr_login_server" {
  value = azurerm_container_registry.this.login_server
}

output "acr_name" {
  value = azurerm_container_registry.this.name
}

output "aks_name" {
  description = "Null while deploy_aks is false."
  value       = var.deploy_aks ? module.aks[0].name : null
}

output "key_vault_name" {
  value = module.security.key_vault_name
}

output "tenant_id" {
  value = module.security.tenant_id
}

output "workload_client_ids" {
  description = "Client ids of the workload identities, annotated onto the Kubernetes service accounts."
  value       = module.security.workload_client_ids
}

output "function_app_name" {
  value = module.functions.name
}

output "postgres_fqdn" {
  value = module.data.fqdn
}
