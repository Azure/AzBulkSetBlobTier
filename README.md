---
page_type: sample
name: AzBulkSetBlobTier
topic: sample
description: |
  AzBulkSetBlobTier is a sample application designed to help Azure Storage customers preform very large data moves from Archive to Hot or Cool Tier.
languages:
  - csharp
products:
  - azure
  - azure-blob-storage
urlFragment: azbulksetblobtier
---

# AzBulkSetBlobTier

** DRAFT **

The goal of this sample application is to show an efficient way to queue all the objects in an Azure Storage Container for moving from Archive to Hot or Cool storage. 

Moving objects from Archive to Hot or Cool storage is a two step process:

- Step 1: you call the SetBlobTier API to enque a request to Azure Storage to perform the move
- Step 2: Azure Storage performs the move

This project is focused on performing Step 1 as fast as possble. 
The time it takes to perform Step 2 is dependent on a number of factors like if you asked for "High priority" rehydration, how busy the tape library is, what size the files you are rehydrating are, how many tapes the files are distributed on, etc.

More info on the rehydration process can be found in the Azure Docs here: [Rehydrate blob data from the archive tier](https://docs.microsoft.com/azure/storage/blobs/storage-blob-rehydration)


In this sample we are sharing 2 options, one using PowerShell and another using a .NET Docker Container. The first option is very lightweight, however doesn't include some of the features of the .NET option. 


## PowerShell Option

This option uses PowerShell to first get a list of all the objects in Azure Storage, and then call SetBlobTier on each Archive Object. 

Recommendations
 - Run this from a VM in the same region as the Storage account to reduce network latency
 - Run multiple copies based on non-overlaping prefix ranges, [see the docs for more details on how the prefix parameter works](https://docs.microsoft.com/powershell/module/az.storage/get-azstorageblob#parameters)


``` powershell
$storageAccountName = ""     # Enter account name
$storageContainer =  ""           # Enter specific container
$prefix = "0x14"                      # Set prefix for scanning
$MaxReturn = 10000
$count = 0
$StorageAccountKey = "" # Enter account/sas key

write-host "Starting script"

$ctx = New-AzStorageContext -StorageAccountName $storageAccountName -StorageAccountKey $StorageAccountKey 
$Token = $Null

do  
{  
    $listOfBlobs = Get-AzStorageBlob -Container $storageContainer -Context $ctx -MaxCount $MaxReturn  -ContinuationToken $Token -Prefix $prefix
  
    foreach($blob in $listOfBlobs) {  
        if($blob.ICloudBlob.Properties.StandardBlobTier -eq "Archive") 
        { 
        $blob.ICloudBlob.SetStandardBlobTier("Hot")
        #write-host "the blob " $blob.name " is being set to Hot"
        $count++
        }
    }  
    $Token = $blob[$blob.Count -1].ContinuationToken;  
    
    write-host "Processed "  ($count)  " items. Continuation token = " $Token.NextMarker
}while ($Null -ne $Token)

write-host "Complete processing of all blobs returned with prefix " $prefix
```

## .NET Docker Container Option

This option leverages:

- [Multi-threaded Architecture](https://docs.microsoft.com/dotnet/api/system.threading.semaphoreslim) to increase total throughput. 
  - Threads are spawned based on the naming convention in your storage account using a `/` as the delimiter, see [here](https://docs.microsoft.com/dotnet/api/azure.storage.blobs.blobcontainerclient.getblobsbyhierarchy) for more info.
- Use of the [Batch API](https://docs.microsoft.com/rest/api/storageservices/blob-batch) to reduce calls to SetBlobTier
- Deployment to an [Azure Container Instance](https://azure.microsoft.com/services/container-instances/) to reduce network latency vs running over the internet
- Monioring with [Application Insights](https://docs.microsoft.com/azure/azure-monitor/app/app-insights-overview)

You can deploy the sample as is or modify it to fit your unique needs.

### Prerequisites

This sample requires the following Azure services and role assignments to be deployed:

- Access to an Azure subscription with appropriate quotas
- Contributor (or equivalent) [RBAC](https://docs.microsoft.com/azure/role-based-access-control/overview) assignments at a subscription scope to create the following Azure services:
  - [Azure Storage Account](https://azure.microsoft.com/services/storage/)
    - [Blob Storage](https://azure.microsoft.com/services/storage/blobs/)
  - [Azure Container Registry](https://azure.microsoft.com/services/container-registry/)
  - [Azure Container Instances](https://azure.microsoft.com/services/container-instances/)
  - [Azure Application Insights](https://docs.microsoft.com/azure/azure-monitor/app/app-insights-overview)

This sample is also dependent on a series of scripts to deploy, the following is required:

- [Azure CLI](https://docs.microsoft.com/cli/azure/what-is-azure-cli?view=azure-cli-latest)

### Build, Deploy & Run the sample

#### Shared Variables used for all deployment steps

I typically run all these commands from the bash [Azure Cloud Shell](https://docs.microsoft.com/azure/cloud-shell/overview)

``` bash
# the region you want to deploy to 
# this should match the region the storage account is in if you already have a storage account
REGION="southcentralus"

# the name of the resource group that you want to deploy the resources to
# the sample assumes everything is in the same resource group, your situation might differ
RG="testrg"

# the name of the storage account you want to rehydrate from/to
STORAGEACCT="testacct"

# the name of the container in the storage account you want to rehydrate from/to
STORAGECNT="testacctct"

# the name you want to use for your Azure Container Registry
ACR="testacr"

# the name you want to use for your Application Insights Instance
AI="testai"

# the name you want to use for the ACI instance
ACI="testaci"
```

#### Deploy the Infrastructure, Build the sample, and publish it to ACR

``` bash
# Create the Resource Group
az group create -n $RG -l $REGION

# Create Storage Account if you dont already have one
#   If you already have one filled with your archived blobs, skip this step
az storage account create --name $STORAGEACCT --access-tier Hot --kind StorageV2 --sku Standard_LRS --https-only true -g $RG -l $REGION

# Create Container Registry
az acr create --name $ACR --admin-enabled true --sku Standard -g $RG -l $REGION

# Create Application Insights
#   If you don't have the AI extension installed in your shell you will be prompted to install it when you run this command
az monitor app-insights component create --app $AI -g $RG -l $REGION

# Package the sample into a docker container and publish it to ACR
#   Here we are building with the published sample code from GitHub
#   If you are changing the code alter this command to point to where you put your code
az acr build -r $ACR https://github.com/Azure/AzBulkSetBlobTier.git -f AzBulkSetBlobTier/Dockerfile --image azbulksetblobtier:latest
```

#### Run the sample

``` bash
# Request authentication information from container registry
ACRSVR="$(az acr show --name $ACR --query loginServer -o tsv)"
ACRUSER="$(az acr credential show --name $ACR --query username  -o tsv)"
ACRPWD="$(az acr credential show --name $ACR --query passwords[0].value -o tsv)"

# Request authentication information from application insights
AIKEY="$(az monitor app-insights component show --app $AI --query instrumentationKey -g $RG -o tsv)"

# Request authentication information from storage account
STORAGEACCTCS="$(az storage account show-connection-string --name $STORAGEACCT -g $RG -o tsv)"

# Deploy & Run an instance of the sample to ACI
az container create \
    --name $ACI \
    --resource-group $RG \
    --location $REGION \
    --cpu 2 \
    --memory 4 \
    --registry-login-server $ACRSVR \
    --registry-username $ACRUSER \
    --registry-password $ACRPWD \
    --image "$ACRSVR/azbulksetblobtier:latest" \
    --restart-policy Never \
    --no-wait \
    --environment-variables \
        APPINSIGHTS_INSTRUMENTATIONKEY=$AIKEY \
        StorageConnectionString=$STORAGEACCTCS \
        Container=$STORAGECNT \
        Prefix="" \
        WhatIf="false" \
        ThreadCount="0" \
        TargetAccessTier="Hot"
```


#### Delete the deployment

When you are all done you can use the following commands to clean up

``` bash
## Delete the running ACI node
az container delete -n $ACI -g $RG -y
```

### Tips

- Deploy the ACI instance to the SAME region your storage account is in. This will reduce network latency on the calls between the app and the storage account.
- Running the application in WhatIf mode is a good way to get an idea of if the files have been read off of archive and put back in your tier of choice (hot/cool). However, it needs to scan each object in the container to do this. For larger containers this will take time and consume storage transactions.
- You can rerun the above bash script with different values for your environmental variables to change them, without needing to delete and recreate the ACI instance.
- You can run multiple instances of ACI (with different names) if you want to process multiple storage accounts/containers at the same time.

### Monitoring

You can run this Kusto query to see logs for the last run.

If you are using AI without Log Analytics (the default for this sample)

``` sql
let runs = dependencies | summarize TimeGenerated=max(timestamp) by run=tostring(customDimensions["Run"]) | order by TimeGenerated | take 1 ;
dependencies
| where tostring(customDimensions["Run"]) in (runs)
| extend run=tostring(customDimensions["Run"])
| order by timestamp 
```

If you are using AI with Log Analytics

``` sql
let runs = AppDependencies | summarize TimeGenerated=max(TimeGenerated) by run=tostring(Properties["Run"]) | order by TimeGenerated | take 1 ;
AppDependencies
| where tostring(Properties["Run"]) in (runs)
| extend run=tostring(Properties["Run"])
| order by TimeGenerated 
```


## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
