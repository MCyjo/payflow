<#
.SYNOPSIS
  One-time setup of the Azure Storage account that holds the Terraform state.

.DESCRIPTION
  Terraform cannot create its own backend, so this is the only resource created outside it.
  Run after `az login`. Writes infra/terraform/backend.hcl (git-ignored) for `terraform init -backend-config=backend.hcl`.
  Access is through Entra ID only (shared keys disabled), so no storage key ever exists to leak.
#>
param(
    [string]$Location = 'westeurope',
    [string]$ResourceGroup = 'rg-payflow-tfstate',
    [string]$StateKey = 'payflow-dev.tfstate'
)

$ErrorActionPreference = 'Stop'

az account show --query id -o tsv | Out-Null   # fails fast when not logged in

$suffix = -join ((97..122) + (48..57) | Get-Random -Count 4 | ForEach-Object { [char]$_ })
$storage = "stpayflowtfstate$suffix"

az group create --name $ResourceGroup --location $Location -o none
az storage account create `
    --name $storage --resource-group $ResourceGroup --location $Location `
    --sku Standard_LRS --min-tls-version TLS1_2 `
    --allow-blob-public-access false --allow-shared-key-access false -o none

# Blob versioning gives a way back from a corrupted or accidentally overwritten state file.
az storage account blob-service-properties update --account-name $storage --resource-group $ResourceGroup --enable-versioning true -o none

$me = az ad signed-in-user show --query id -o tsv
$scope = az storage account show --name $storage --resource-group $ResourceGroup --query id -o tsv
az role assignment create --assignee-object-id $me --assignee-principal-type User `
    --role 'Storage Blob Data Contributor' --scope $scope -o none

Write-Host 'Waiting for the role assignment to propagate...'
Start-Sleep -Seconds 45
az storage container create --name tfstate --account-name $storage --auth-mode login -o none

$backend = Join-Path $PSScriptRoot '..\terraform\backend.hcl'
@"
resource_group_name  = "$ResourceGroup"
storage_account_name = "$storage"
container_name       = "tfstate"
key                  = "$StateKey"
use_azuread_auth     = true
"@ | Set-Content -Path $backend -Encoding utf8

Write-Host "State storage ready: $storage"
Write-Host "Next: cd infra/terraform; terraform init -backend-config=backend.hcl"
