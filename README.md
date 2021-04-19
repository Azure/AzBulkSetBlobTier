# AzBulkSetBlobTier

** DRAFT **

The goal of this sample application is to show an efficient way to queue all the objects in an Azure Storage Container for moving from Archive to Hot or Cool storage. 

Moving objects from Archive to Hot or Cool storage is a two step process:

- Step 1: you call the SetBlobTier API to enque a request to Azure Storage to perform the move
- Step 2: Azure Storage performs the move

This project is focused on performing Step 1 as fast as possble. 
The time it takes to perform Step 2 is dependent on a number of factors like if you asked for "High priority" rehydration, how busy the tape library is, what size the files you are rehydrating are, how many tapes the files are distributed on, etc.

More info on the rehydration process can be found in the Azure Docs here: [Rehydrate blob data from the archive tier](https://docs.microsoft.com/azure/storage/blobs/storage-blob-rehydration)

The sample leverages:

- [Multi-threaded Architecture](https://docs.microsoft.com/dotnet/api/system.threading.semaphoreslim) to increase total throughput. Threads are spawned based on the naming convention in your storage account using an / as the delimiter, see [here](https://docs.microsoft.com/dotnet/api/azure.storage.blobs.blobcontainerclient.getblobsbyhierarchy) for more info.
- Use of the [Batch API](https://docs.microsoft.com/rest/api/storageservices/blob-batch) to reduce calls to Azure
- Deployment to an [Azure Container Instance](https://azure.microsoft.com/services/container-instances/) to reduce network latency vs running over the internet
- Monioring with [Application Insights](https://docs.microsoft.com/azure/azure-monitor/app/app-insights-overview)

You can deploy the sample as is or modify it to fit your unique needs.

## Prerequisites

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

## Deploy the Infrastructure, Build the docker container, and publish it to ACR

I typically run all these commands from the bash [Azure Cloud Shell](https://docs.microsoft.com/azure/cloud-shell/overview)

```
# Variables
REGION="southcentralus"
RG="mytestrg"
STORAGEACCT="sweisfelatest01"
ACR="sweisfelmyacr"
AI="sweisfelmyai"

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

# Build the docker Container and Publish it to ACR
#   Here we are building with the published sample code from GitHub
#   If you are changing the code alter this command to point to where you put your code
az acr build -r $ACR https://github.com/Azure/AzBulkSetBlobTier.git -f AzBulkSetBlobTier/Dockerfile --image azbulksetblobtier:latest





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
