using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

namespace Kamus.KeyManagement
{
    public class AwsKeyManagement : IKeyManagement
    {
        private readonly IAmazonKeyManagementService mAmazonKeyManagementService;
        private readonly string mCmkPrefix;

        public AwsKeyManagement(IAmazonKeyManagementService amazonKeyManagementService, string cmkPrefix = "")
        {
            mAmazonKeyManagementService = amazonKeyManagementService;
            mCmkPrefix = cmkPrefix;
        }

        public async Task<string> Encrypt(string data, string serviceAccountId, bool createKeyIfMissing = true)
        {
            var cmkPrefix = string.IsNullOrEmpty(mCmkPrefix) ? "" : $"{mCmkPrefix}-"; 
            var masterKeyAlias = $"alias/{cmkPrefix}kamus/{KeyIdCreator.Create(serviceAccountId)}";
            var (dataKey, encryptedDataKey) = await GenerateEncryptionKey(masterKeyAlias);

            var (encryptedData, iv) = RijndaelUtils.Encrypt(dataKey.ToArray(), Encoding.UTF8.GetBytes(data));

            return EnvelopeEncryptionUtils.Wrap(encryptedDataKey, iv, encryptedData);

        }

        public async Task<string> Decrypt(string encryptedData, string serviceAccountId)
        {
            var (encryptedDataKey, iv, actualEncryptedData) = EnvelopeEncryptionUtils.Unwrap(encryptedData);

            var decryptionResult = await mAmazonKeyManagementService.DecryptAsync(new DecryptRequest
            {
                CiphertextBlob = new MemoryStream(Convert.FromBase64String(encryptedDataKey)),
            });

            var decrypted = RijndaelUtils.Decrypt(decryptionResult.Plaintext.ToArray(), iv, actualEncryptedData);

            return Encoding.UTF8.GetString(decrypted);
        }

        private async Task<(MemoryStream dataKey, string encryptedDataKey)> GenerateEncryptionKey(string keyAlias)
        {
            GenerateDataKeyResponse generateKeyResponse = null;
            try
            {
                generateKeyResponse = await mAmazonKeyManagementService.GenerateDataKeyAsync(new GenerateDataKeyRequest { KeyId = keyAlias, KeySpec = "AES_256"});

            }
            catch (NotFoundException)
            {
                await GenerateMasterKey(keyAlias);
                generateKeyResponse = await mAmazonKeyManagementService.GenerateDataKeyAsync(new GenerateDataKeyRequest { KeyId = keyAlias, KeySpec = "AES_256"});
            }

            if (generateKeyResponse.HttpStatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"Couldn't generate encryption key for {keyAlias}");
            }

            return (generateKeyResponse.Plaintext, ConvertMemoryStreamToBase64String(generateKeyResponse.CiphertextBlob));
        }

        private static string ConvertMemoryStreamToBase64String(MemoryStream ms)
        {
            return Convert.ToBase64String(ms.ToArray());
        }

        private async Task GenerateMasterKey(string keyAlias)
        {
            var createKeyResponse = await mAmazonKeyManagementService.CreateKeyAsync(new CreateKeyRequest());
            await mAmazonKeyManagementService.CreateAliasAsync(keyAlias, createKeyResponse.KeyMetadata.KeyId);
        }
    }
}