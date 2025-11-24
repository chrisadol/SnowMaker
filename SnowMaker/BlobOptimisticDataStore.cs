using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnowMaker
{
    public class BlobOptimisticDataStore : IOptimisticDataStore
    {
        const string SeedValue = "1";

        readonly BlobContainerClient _blobContainerClient;

        readonly IDictionary<string, BlobClient> _blobReferences;
        readonly SemaphoreSlim _blobReferencesSemaphore = new SemaphoreSlim(1,1);

        public static async Task<BlobOptimisticDataStore> CreateAsync(string connectionString, string containerName) 
        {
            var ret = new BlobOptimisticDataStore(connectionString, containerName);
            return await ret.InitializeAsync();
        }

        public static async Task<BlobOptimisticDataStore> CreateAsync(Uri serviceUri, AzureSasCredential azureSasCredential, string containerName)
        {
            var ret = new BlobOptimisticDataStore(serviceUri, azureSasCredential, containerName);
            return await ret.InitializeAsync();
        }
        public static async Task<BlobOptimisticDataStore> CreateAsync(Uri serviceUri, StorageSharedKeyCredential storageSharedKeyCredential, string containerName)
        {
            var ret = new BlobOptimisticDataStore(serviceUri, storageSharedKeyCredential, containerName);
            return await ret.InitializeAsync();
        }
        public static async Task<BlobOptimisticDataStore> CreateAsync(Uri serviceUri, TokenCredential tokenCredential, string containerName)
        {
            var ret = new BlobOptimisticDataStore(serviceUri, tokenCredential, containerName);
            return await ret.InitializeAsync();
        }
        private async Task<BlobOptimisticDataStore> InitializeAsync()
        {
            await _blobContainerClient.CreateIfNotExistsAsync();
            return this;
        }

        private BlobOptimisticDataStore(BlobServiceClient blobServiceClient, string containerName)
        {
            _blobContainerClient= blobServiceClient.GetBlobContainerClient(containerName.ToLower());
            //can't call CreateIfNotExistsAsync in the constructor, hence the static CreateAsync versions above

            _blobReferences = new ConcurrentDictionary<string, BlobClient>(); //This passes parallel integration tests without using ConcurrentDictionary
        }

        private BlobOptimisticDataStore(string connectionString, string containerName) : 
            this(new BlobServiceClient(connectionString), containerName)
        {

        }

        private BlobOptimisticDataStore(Uri serviceUri, AzureSasCredential azureSasCredential, string containerName)
            : this(new BlobServiceClient(serviceUri, azureSasCredential), containerName)
        {
        }


        private BlobOptimisticDataStore(Uri serviceUri, StorageSharedKeyCredential storageSharedKeyCredential, string containerName)
            : this(new BlobServiceClient(serviceUri, storageSharedKeyCredential), containerName)
        {

        }

        private BlobOptimisticDataStore(Uri serviceUri, TokenCredential tokenCredential,  string containerName)
            : this (new BlobServiceClient(serviceUri, tokenCredential), containerName)
        {

        }



        public async Task<string> GetDataAsync(string blockName)
        {
            var blobReference = await GetBlobReferenceAsync(blockName);
            using (var stream = new MemoryStream())
            {
                await blobReference.DownloadToAsync(stream);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public async Task<bool> TryOptimisticWriteAsync(string scopeName, string data)
        {
            var blobReference = await GetBlobReferenceAsync(scopeName);
            try
            {
                var blobProperties = await blobReference.GetPropertiesAsync();                
                BlobRequestConditions accessConditions = new BlobRequestConditions();                
                accessConditions.IfMatch = blobProperties.Value.ETag; 

                await UploadTextAsync(blobReference, data, accessConditions);
            }
            catch (RequestFailedException exc)
            {
                System.Diagnostics.Debug.WriteLine($"TryOptimisticWriteAsync RequestFailedException {exc.ErrorCode} {exc.Status} {exc.InnerException}");
                if (exc.Status == (int)HttpStatusCode.PreconditionFailed)
                    return false;

                throw;
            }
            return true;
        }

        private async Task<BlobClient> GetBlobReferenceAsync(string blockName)
        {
            return await _blobReferences.GetValueAsync(
                blockName,
                _blobReferencesSemaphore,
                async () => await InitializeBlobReference(blockName));
        }

        private async Task<BlobClient> InitializeBlobReference(string blockName)
        {
            var blobReference = _blobContainerClient.GetBlobClient(blockName);

            var exists = await blobReference.ExistsAsync();
            if (exists) {
                return blobReference;
            }

            try
            {
                BlobRequestConditions blobRequestConditions =
                    new BlobRequestConditions();
                blobRequestConditions.IfNoneMatch = new ETag("*");
                await UploadTextAsync(blobReference, SeedValue, blobRequestConditions);
            }
            catch (RequestFailedException uploadException)
            {
                if (uploadException.Status != (int)HttpStatusCode.Conflict)
                    throw;
            }

            return blobReference;
        }

        private async Task UploadTextAsync(BlobClient blob, string text, BlobRequestConditions accessCondition)
        {
            var blobUploadOptions = new BlobUploadOptions();
            blobUploadOptions.Conditions = accessCondition;
            blobUploadOptions.HttpHeaders = new BlobHttpHeaders();
            blobUploadOptions.HttpHeaders.ContentType = "text/plain";
            blobUploadOptions.HttpHeaders.ContentEncoding = "UTF-8";

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                await blob.UploadAsync(stream, blobUploadOptions);
            }
        }
    }
}
