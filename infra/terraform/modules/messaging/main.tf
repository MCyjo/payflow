variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "tags" { type = map(string) }

locals {
  topic_name = "domain-events"

  # Filters match on the message Subject, which the publisher sets to the event type.
  # A subscription that is not listed keeps the broker's default rule and receives everything (audit).
  subscriptions = {
    "payment-service"       = "sys.Label = 'OrderCreated'"
    "order-service"         = "sys.Label IN ('PaymentCompleted', 'PaymentFailed')"
    "notification-function" = "sys.Label IN ('PaymentCompleted', 'PaymentFailed')"
    "audit"                 = null
  }

  filtered_subscriptions = { for name, filter in local.subscriptions : name => filter if filter != null }
}

resource "azurerm_servicebus_namespace" "this" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = "Standard" # Basic has no topics and no duplicate detection
  minimum_tls_version = "1.2"

  # Entra ID only: no shared access keys exist, so there is nothing to leak. Apps use managed identities.
  local_auth_enabled = false

  tags = var.tags
}

# Commands: point-to-point work for the payment service.
resource "azurerm_servicebus_queue" "payment_commands" {
  name         = "payment-commands"
  namespace_id = azurerm_servicebus_namespace.this.id

  lock_duration                           = "PT1M"
  max_delivery_count                      = 5
  dead_lettering_on_message_expiration    = true
  default_message_ttl                     = "P1D"
  requires_duplicate_detection            = true
  duplicate_detection_history_time_window = "PT10M"
}

# Events: pub/sub fan-out. MessageId == EventId, so the broker drops a re-published duplicate inside the window.
resource "azurerm_servicebus_topic" "domain_events" {
  name         = local.topic_name
  namespace_id = azurerm_servicebus_namespace.this.id

  default_message_ttl                     = "P1D"
  requires_duplicate_detection            = true
  duplicate_detection_history_time_window = "PT10M"
}

resource "azurerm_servicebus_subscription" "this" {
  for_each = local.subscriptions

  name     = each.key
  topic_id = azurerm_servicebus_topic.domain_events.id

  lock_duration                        = "PT1M"
  max_delivery_count                   = 5 # then the message moves to the subscription's dead-letter queue
  dead_lettering_on_message_expiration = true
}

# Every subscription is created with a rule named "$Default" that accepts all messages. Adding a second rule
# next to it would leave the subscription unfiltered, and azurerm has no resource for replacing it. An ARM
# deployment can PUT the rule under the same name, which overwrites the filter in place.
resource "azurerm_resource_group_template_deployment" "subscription_filters" {
  name                = "${var.name}-subscription-filters"
  resource_group_name = var.resource_group_name
  deployment_mode     = "Incremental"

  template_content = jsonencode({
    "$schema"      = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"
    contentVersion = "1.0.0.0"
    resources = [
      for sub, filter in local.filtered_subscriptions : {
        type       = "Microsoft.ServiceBus/namespaces/topics/subscriptions/rules"
        apiVersion = "2022-10-01-preview"
        name       = "${azurerm_servicebus_namespace.this.name}/${local.topic_name}/${sub}/$Default"
        properties = {
          filterType = "SqlFilter"
          sqlFilter  = { sqlExpression = filter }
        }
      }
    ]
  })

  depends_on = [azurerm_servicebus_subscription.this]
}

output "namespace_id" { value = azurerm_servicebus_namespace.this.id }
output "namespace_name" { value = azurerm_servicebus_namespace.this.name }
output "fully_qualified_namespace" { value = "${azurerm_servicebus_namespace.this.name}.servicebus.windows.net" }
