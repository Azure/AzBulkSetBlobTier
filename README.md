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

The goal of this sample application is to show an efficient way to queue all the objects in an Azure Storage Container for moving from/to any of the storage tiers Hot, Cool or Archive. 

> If you just need to move to a cooler tier (i.e. hot -> cool, hot -> archive, cool -> archive), take a look at [Lifecycle Management](https://docs.microsoft.com/azure/storage/blobs/storage-lifecycle-management-concepts). Lifecycle Management is a officially support Azure feature. 

Moving objects storage tiers is a two step process:

- Step 1: you call the SetBlobTier API to enqueue a request to Azure Storage to perform the move
- Step 2: Azure Storage performs the move

This project is focused on performing Step 1 as fast as possible. 

The time it takes to perform Step 2 is dependent on several factors. For example if you are moving from Archive and if you asked for "High priority" rehydration, how busy the back end is, what size the files you are rehydrating are, how well the files are distributed, etc.

More info on the rehydration process from Archive can be found in the Azure Docs here: [Rehydrate blob data from the archive tier](https://docs.microsoft.com/azure/storage/blobs/storage-blob-rehydration)


In this sample we are sharing 2 options, one using PowerShell and another using a .NET Docker Container. The first option is very lightweight, however doesn't include some of the features of the .NET option. 

## Considerations

Both of these options require iterating over all the objects in the storage account and calling the API to request that the objects be moved to a different tier. The number of files you have will drive how long this process will take and how many transactions you will consume. Moreover, the data retrieval costs are based on the amount of data you need to restore. Consult the [Pricing section](https://docs.microsoft.com/azure/storage/blobs/storage-blob-rehydration?#pricing-and-billing) of the Azure Docs to learn more.


## PowerShell Option

This option uses PowerShell to first get a list of all the objects in Azure Storage, and then call SetBlobTier on each Archive Object. 

Recommendations
 - Run this from a VM in the same region as the Storage account to reduce network latency
 - Run multiple copies based on non-overlapping prefix ranges, [see the docs for more details on how the prefix parameter works](https://docs.microsoft.com/powershell/module/az.storage/get-azstorageblob#parameters)


``` powershell
$storageAccountName = ""     # Enter account name
$storageContainer =  ""           # Enter specific container
$prefix = "a"                      # Set prefix for scanning
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
  - Threads are spawned based on the naming convention in your storage account using a delimiter. 
  - By default we use a `/` but it can be modified via configuration. See [here](https://docs.microsoft.com/dotnet/api/azure.storage.blobs.blobcontainerclient.getblobsbyhierarchy) for more info.
- Use of the [Batch API](https://docs.microsoft.com/rest/api/storageservices/blob-batch) to reduce calls to SetBlobTier
- Deployment to an [Azure Container Instance](https://azure.microsoft.com/services/container-instances/) to reduce network latency vs running over the internet
- Monitoring with [Application Insights](https://docs.microsoft.com/azure/azure-monitor/app/app-insights-overview)

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
        WhatIf="false" \
        SourceAccessTier="Archive" \
        TargetAccessTier="Hot"
```


#### Run the sample for each container in the storage account

This will get a list of storage containers from the storage account and create one ACI instance to work on each container.

``` bash
# Request authentication information from container registry
ACRSVR="$(az acr show --name $ACR --query loginServer -o tsv)"
ACRUSER="$(az acr credential show --name $ACR --query username  -o tsv)"
ACRPWD="$(az acr credential show --name $ACR --query passwords[0].value -o tsv)"

# Request authentication information from application insights
AIKEY="$(az monitor app-insights component show --app $AI --query instrumentationKey -g $RG -o tsv)"

# Request authentication information from storage account
STORAGEACCTCS="$(az storage account show-connection-string --name $STORAGEACCT -g $RG -o tsv)"

# Deploy & Run an instance of the sample to ACI for each storage container
for container in `az storage container list --connection-string $STORAGEACCTCS -o tsv --query [].name`; do
  az container create \
      --name $ACI$RANDOM \
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
          Container=$container \
          WhatIf="false" \
          SourceAccessTier="Archive" \
          TargetAccessTier="Hot"
done
```

#### Configuration Options

- StorageConnectionString - full connection string to the storage account
- Container - name of the storage container you want to look in
- Prefix - Filters the results to return only blobs whose names begin with the specified prefix
  - [More Info](https://docs.microsoft.com/rest/api/storageservices/list-blobs)
- Delimiter - Used to break the run into different chunks that can be partitioned to different threads
  - by default a `\` is used
  - you might choose to use a different value if you use a different character in your path. For example you might choose to use a `-` if you have guids in your path. 
- SourceAccessTier - the tier to move blobs FROM
- TargetAccessTier - the tier to move blobs TO
- ThreadCount - the number of threads to use
  - by default this is the number of cores * 8
- WhatIf - if you set this to true, the app will iterate over the account and do all the loging, it just will not make any changes to the blob tiers. If you set it to false the app will request changes to blob tiers. 


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

The tool outputs logs to Application Insights. This allows you a robust tool to monitor many instances of this tool running in parallel. 

The tool logs the following operation types and additional properties/metrics to the dependencies/AppDependencies table in Application Insights.

- Setup
  - Run - this property is a GUID generated when the docker container starts. It allows you to tie all the logs for a given run together. There should be 1 of these per instance. Written when the job starts.  
  - Delimiter - this property lists the configured Delimiter value
  - Prefix - this property lists the configured Prefix value
  - Container - this property lists the configured Container value
  - StorageAccountName - this property lists the configured Storage Account Name value
  - ThreadCount - this property lists the configured ThreadCount value
  - WhatIf - this property lists the configured WhatIf value
  - TargetAccessTier - this property lists the configured TargetAccessTier value
  - SourceAccessTier - this property lists the configured SourceAccessTier value
- Do Work - for the entire storage container. There should be 1 of these per instance. Written when the job ends.
  - Run - this property is a GUID generated when the docker container starts. It allows you to tie all the logs for a given run together. 
  - Blobs - total number of block blobs
  - Bytes - total size of block blobs
  - Hot Blobs - total number of hot block blobs before any blobs were moved
  - Hot Bytes - total size of hot block blobs before any blobs were moved
  - Cool Blobs - total number of cool block blobs before any blobs were moved
  - Cool Bytes - total number of cool block blobs before any blobs were moved
  - Archive Blobs - total number of archive block blobs before any blobs were moved
  - Archive Bytes - total number of archive block blobs before any blobs were moved
  - Archive To Hot Blobs - total number of hot block blobs pending move from a prior request, these blobs are ignored by the tool since they have a pending request on them
  - Archive To Hot Bytes - total number of hot block blobs pending move from a prior request, these blobs are ignored by the tool since they have a pending request on them
  - Archive To Cool Blobs - total number of hot block blobs pending move from a prior request, these blobs are ignored by the tool since they have a pending request on them
  - Archive To Cool Bytes - total number of hot block blobs pending move from a prior request, these blobs are ignored by the tool since they have a pending request on them
- ProcessPrefix - for just the current prefix - use this to monitor how well you are multi threading. There should be one of these per prefix scanned. Written after that prefix is done. 
  - Run - this property is a GUID generated when the docker container starts. It allows you to tie all the logs for a given run together. 
  - Prefix - the path that this thread is processing, current only, doesn't include any sub paths
- ProcessBatch - a batch of files that we requested moves for. There should be one of these for each batch of files that the tool needs to request a move for.
  - Run - this property is a GUID generated when the docker container starts. It allows you to tie all the logs for a given run together. 
  - BatchSize - the number of files in the batch, should be no bigger than 250


You can run this Kusto query to see logs for the last run.

If you are using AI without Log Analytics (the default for this sample)

``` sql
// Setup information for each run
dependencies
| where name == 'Setup'
| project timestamp,
     Run=tostring(customDimensions["Run"]), 
     Delimiter=tostring(customDimensions["Delimiter"]), 
     Prefix=tostring(customDimensions["Prefix"]),
     Container=tostring(customDimensions["Container"]),
     StorageAccountName=tostring(customDimensions["StorageAccountName"]),
     ThreadCount=tostring(customDimensions["ThreadCount"]),
     WhatIf=tostring(customDimensions["WhatIf"]),
     SourceAccessTier=tostring(customDimensions["SourceAccessTier"]),
     TargetAccessTier=tostring(customDimensions["TargetAccessTier"])
| order by timestamp 

// Status for each run
dependencies
| where name == "Do Work"
| project timestamp,
     duration,
     Run=tostring(customDimensions["Run"]), 
     Blobs=tolong(customMeasurements["Blobs"]),
     GiB=round(tolong(customMeasurements["Bytes"])/exp2(30),2),
     HotBlobs=tolong(customMeasurements["Hot Blobs"]),
     HotGiB=round(tolong(customMeasurements["Hot Bytes"])/exp2(30),2),
     CoolBlobs=tolong(customMeasurements["Cool Blobs"]),
     CoolGiB=round(tolong(customMeasurements["Cool Bytes"])/exp2(30),2),
     ArchiveBlobs=tolong(customMeasurements["Archive Blobs"]),
     ArchiveGiB=round(tolong(customMeasurements["Archive Bytes"])/exp2(30),2),
     ArchiveToCoolBlobs=tolong(customMeasurements["Archive To Cool Blobs"]),
     ArchiveToCoolGiB=round(tolong(customMeasurements["Archive To Cool Bytes"])/exp2(30),2),
     ArchiveToHotBlobs=tolong(customMeasurements["Archive To Hot Blobs"]),
     ArchiveToHotGiB=round(tolong(customMeasurements["Archive To Hot Bytes"])/exp2(30),2)
| order by timestamp

// Details of each prefix/thread for the last run
let runs = dependencies | summarize TimeGenerated=max(timestamp) by run=tostring(customDimensions["Run"]) | order by TimeGenerated | take 1 ;
dependencies
| where tostring(customDimensions["Run"]) in (runs)
    and target == 'ProcessPrefix'
| project timestamp,
    duration,
    run=tostring(customDimensions["Run"]),
    Prefix=tostring(customDimensions["Prefix"])
| order by timestamp 

// Details of each batch for the last run
let runs = dependencies | summarize TimeGenerated=max(timestamp) by run=tostring(customDimensions["Run"]) | order by TimeGenerated | take 1 ;
dependencies
| where tostring(customDimensions["Run"]) in (runs)
    and target == 'ProcessBatch'
| project timestamp,
    duration,
    run=tostring(customDimensions["Run"]),
    BatchSize=tolong(customMeasurements["BatchSize"])
| order by timestamp 
```

> NOTE: If you are using AI with Log Analytics, the `dependencies` table is called `AppDependencies` and some fo the column names are different. 

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
