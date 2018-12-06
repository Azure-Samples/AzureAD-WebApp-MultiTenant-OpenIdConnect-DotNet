﻿/************************************************************************************************
The MIT License (MIT)

Copyright (c) 2015 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
***********************************************************************************************/

using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TodoListWebApp.Utils
{
    /// <summary>
    /// Generic class that validates token issuer from the provided Azure AD authority
    /// </summary>
    public static class AadIssuerValidator
    {
        /// <summary>
        /// A list of all Issuers across the various Azure AD instances
        /// </summary>
        private static readonly SortedSet<string> IssuerAliases = new SortedSet<string>();
        const string FallBackAuthority = "https://login.microsoftonline.com/";

        static AadIssuerValidator()
        {
            // In the constructor, we hit the Azure Ad issuer metadata endpoint and cache the aliases. The data is cached for 24 hrs by default.
            string AzureADIssuerMetadataUrl = "https://login.microsoftonline.com/common/discovery/instance?authorization_endpoint=https://login.microsoftonline.com/common/oauth2/v2.0/authorize&api-version=1.1";
            ConfigurationManager<IssuerMetadata> configManager = new ConfigurationManager<IssuerMetadata>(AzureADIssuerMetadataUrl, new IssuerConfigurationRetriever());
            IssuerMetadata issuerMetadata = configManager.GetConfigurationAsync().Result;

            // Add issuer aliases of the chosen authority
            string authority = !string.IsNullOrWhiteSpace(Startup.AadInstance) ? Startup.AadInstance : FallBackAuthority;
            IssuerAliases.Clear();
            issuerMetadata.Metadata.FirstOrDefault(auth => $"https://{auth.PreferredNetwork}/" == authority)?.Aliases.ForEach(z => IssuerAliases.Add(z));
        }

        /// <summary>
        /// Validate the issuer for multi-tenant applications of various audience (Work and School account, or Work and School accounts +
        /// Personal accounts)
        /// </summary>
        /// <param name="issuer">Issuer to validate (will be tenanted)</param>
        /// <param name="securityToken">Received Security Token</param>
        /// <param name="validationParameters">Token Validation parameters</param>
        /// <remarks>The issuer is considered as valid if it has the same http scheme and authority as the
        /// authority from the configuration file, has a tenant Id, and optionally v2.0 (this web api
        /// accepts both V1 and V2 tokens).
        /// Authority aliasing is also taken into account</remarks>
        /// <returns>The <c>issuer</c> if it's valid, or otherwise <c>SecurityTokenInvalidIssuerException</c> is thrown</returns>
        public static string ValidateAadIssuer(string issuer, SecurityToken securityToken, TokenValidationParameters validationParameters)
        {
            JwtSecurityToken jwtToken = securityToken as JwtSecurityToken;
            if (jwtToken == null)
            {
                throw new ArgumentNullException(nameof(securityToken), $"{nameof(securityToken)} cannot be null.");
            }

            if (validationParameters == null)
            {
                throw new ArgumentNullException(nameof(validationParameters), $"{nameof(validationParameters)} cannot be null.");
            }

            // Extract the tenant Id from the claims
            string tenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new SecurityTokenInvalidIssuerException("The `tid` claim is not present in the token obtained from Azure Active Directory.");
            }

            // Build a list of valid tenanted issuers from the provided TokenValidationParameters.
            List<string> allValidTenantedIssuers = new List<string>();

            IEnumerable<string> validIssuers = validationParameters.ValidIssuers;
            if (validIssuers != null)
            {
                allValidTenantedIssuers.AddRange(validIssuers.Select(i => TenantedIssuer(i, tenantId)));
            }

            if (validationParameters.ValidIssuer != null)
            {
                allValidTenantedIssuers.Add(TenantedIssuer(validationParameters.ValidIssuer, tenantId));
            }

            // Remove aliases from the valid tenanted issuers and build a list of tenantID guid's only for comparison
            List<string> allValidIssuers = new List<string>();
            allValidIssuers.AddRange(allValidTenantedIssuers.Select(ExtractTenantIdFromIssuerString));
            
            if (!allValidIssuers.Contains(ExtractTenantIdFromIssuerString(issuer)))
            {
                throw new SecurityTokenInvalidIssuerException("Issuer does not match any of the valid issuers provided for this application.");
            }
            
            return issuer;
        }

        /// <summary>
        /// Extracts the tenantId from a set of possible issuer strings
        /// </summary>
        /// <param name="tenantedIssuer">Full issuer string</param>
        /// <returns>A string with just the tenantId guid</returns>
        private static string ExtractTenantIdFromIssuerString(string tenantedIssuer)
        {
            string tenantId = IssuerAliases.Aggregate(tenantedIssuer, (str, filter) => str.Replace(filter, ""));

            return tenantId.Replace("v2.0", string.Empty).Replace("/", string.Empty);
        }

        private static string TenantedIssuer(string i, string tenantId)
        {
            return i.Replace("{tenantid}", tenantId);
        }
    }

    /// <summary>
    /// An implementation of IConfigurationRetriever geared towards Azure AD issuers metadata />
    /// </summary>
    public class IssuerConfigurationRetriever : IConfigurationRetriever<IssuerMetadata>
    {
        /// <summary>Retrieves a populated configuration given an address and an <see cref="T:Microsoft.IdentityModel.Protocols.IDocumentRetriever"/>.</summary>
        /// <param name="address">Address of the discovery document.</param>
        /// <param name="retriever">The <see cref="T:Microsoft.IdentityModel.Protocols.IDocumentRetriever"/> to use to read the discovery document.</param>
        /// <param name="cancel">A cancellation token that can be used by other objects or threads to receive notice of cancellation. <see cref="T:System.Threading.CancellationToken"/>.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException">address - Azure AD Issuer metadata address url is required
        /// or
        /// retriever - No metadata document retriever is provided</exception>
        public async Task<IssuerMetadata> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentNullException(nameof(address), $"Azure AD Issuer metadata address url is required");

            if (retriever == null)
            {
                throw new ArgumentNullException(nameof(retriever), $"No metadata document retriever is provided");
            }

            string doc = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
            IssuerMetadata metadata = JsonConvert.DeserializeObject<IssuerMetadata>(doc);

            return metadata;
        }
    }

    /// <summary>
    /// Model class to hold information parsed from the Azure AD issuer endpoint
    /// </summary>
    public class IssuerMetadata
    {
        [JsonProperty(PropertyName = "tenant_discovery_endpoint")]
        public string TenantDiscoveryEndpoint { get; set; }

        [JsonProperty(PropertyName = "api-version")]
        public string ApiVersion { get; set; }

        [JsonProperty(PropertyName = "metadata")]
        public List<Metadata> Metadata { get; set; }
    }

    /// <summary>
    /// Model child class to hold alias information parsed from the Azure AD issuer endpoint.
    /// </summary>
    public class Metadata
    {
        [JsonProperty(PropertyName = "preferred_network")]
        public string PreferredNetwork { get; set; }

        [JsonProperty(PropertyName = "preferred_cache")]
        public string PreferredCache { get; set; }

        [JsonProperty(PropertyName = "aliases")]
        public List<string> Aliases { get; set; }
    }
}