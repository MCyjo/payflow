variable "subscription_id" {
  description = "Azure subscription to deploy into."
  type        = string
}

variable "project" {
  description = "Short name used in resource names."
  type        = string
  default     = "payflow"
}

variable "environment" {
  description = "Environment name (dev, test, prod)."
  type        = string
  default     = "dev"
}

variable "location" {
  description = "Azure region. Check that the region offers the PostgreSQL SKU and has vCPU quota for the AKS node size."
  type        = string
  default     = "swedencentral"
}

variable "deploy_aks" {
  description = <<-EOT
    Create the AKS cluster and the workload identity federation. This is by far the most expensive part
    (~30 USD/month for one B2s node), so it is off by default: `terraform apply -var deploy_aks=true` for a
    working session and `terraform apply -var deploy_aks=false` (or `az aks stop`) afterwards.
  EOT
  type        = bool
  default     = false
}

variable "aks_node_vm_size" {
  description = "VM size of the single AKS node. Must be available in the region and within the subscription's vCPU quota."
  type        = string
  default     = "Standard_B2s"
}

variable "postgres_sku" {
  description = "PostgreSQL Flexible Server SKU."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "log_daily_quota_gb" {
  description = "Hard daily ingestion cap for Log Analytics, so a noisy service cannot run up the bill."
  type        = number
  default     = 0.5
}

variable "k8s_namespace" {
  description = "Kubernetes namespace the services run in (part of the federated credential subject)."
  type        = string
  default     = "payflow"
}

variable "postgres_location" {
  description = <<-EOT
    Region for PostgreSQL only; defaults to `location`. Burstable SKUs are sometimes out of capacity in a region
    (RegionalAllocationFailed), and the database can live elsewhere at the cost of a little cross-region latency.
  EOT
  type        = string
  default     = null
}
