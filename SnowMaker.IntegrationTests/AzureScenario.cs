using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage;
using Azure.Storage.Blobs;
using NUnit.Framework;

namespace SnowMaker.IntegrationTests
{
    [TestFixture]
    public class AzureScenario : Scenarios<AzureScenario.TestScope>
    {

        private const string DevelopmentStorageAccount = "UseDevelopmentStorage=true"; 

        protected override TestScope BuildTestScope()
        {
            return new TestScope(DevelopmentStorageAccount);
        }

        protected override async Task<IOptimisticDataStore> BuildStoreAsync(TestScope scope)
        {
            return await BlobOptimisticDataStore.CreateAsync(DevelopmentStorageAccount, scope.ContainerName);
        }

        public class TestScope : ITestScope
        {
            readonly BlobServiceClient blobServiceClient;

            public TestScope(string connectionString)
            {
                var ticks = DateTime.UtcNow.Ticks;
                IdScopeName = string.Format("snowmakertest{0}", ticks);
                ContainerName = string.Format("snowmakertest{0}", ticks);

                blobServiceClient = new BlobServiceClient(connectionString);
            }

            public string IdScopeName { get; private set; }
            public string ContainerName { get; private set; }

            public async Task<string> ReadCurrentPersistedValueAsync()
            {
                var blobContainerClient = blobServiceClient.GetBlobContainerClient(ContainerName.ToLower());
                var blobClient = blobContainerClient.GetBlobClient(IdScopeName);
                using (var stream = new MemoryStream())
                {
                    await blobClient.DownloadToAsync(stream);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }

            public void Dispose()
            {
                var blobContainerClient = blobServiceClient.GetBlobContainerClient(ContainerName.ToLower());
                AsyncHelper.RunSync(() => blobContainerClient.DeleteAsync());
            }
        }
    }
}
