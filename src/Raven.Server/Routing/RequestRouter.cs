﻿// -----------------------------------------------------------------------
//  <copyright file="RequestRouter.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

using Raven.Server.Config;
using Raven.Server.Config.Attributes;
using Raven.Server.Documents;
using System.Threading;
using Microsoft.Extensions.Primitives;
using Raven.Client;
using Raven.Client.Exceptions.Security;
using Raven.Client.Server.Operations.ApiKeys;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Raven.Server.Web;
using Raven.Server.Web.Authentication;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Routing
{
    public class RequestRouter
    {
        private readonly Trie<RouteInformation> _trie;
        private readonly RavenServer _ravenServer;
        private readonly MetricsCountersManager _serverMetrics;

        public RequestRouter(Dictionary<string, RouteInformation> routes, RavenServer ravenServer)
        {
            _trie = Trie<RouteInformation>.Build(routes);
            _ravenServer = ravenServer;
            _serverMetrics = ravenServer.Metrics;

        }

        public RouteInformation GetRoute(string method, string path, out RouteMatch match)
        {
            var tryMatch = _trie.TryMatch(method, path);
            match = tryMatch.Match;
            return tryMatch.Value;
        }

        public async Task<string> HandlePath(HttpContext context, string method, string path)
        {
            var tryMatch = _trie.TryMatch(method, path);
            if (tryMatch.Value == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;

                using (var ctx = JsonOperationContext.ShortTermSingleUse())
                using (var writer = new BlittableJsonTextWriter(ctx, context.Response.Body))
                {
                    ctx.Write(writer,
                        new DynamicJsonValue
                        {
                            ["Type"] = "Error",
                            ["Error"] = $"There is no handler for path: {method} {path}{context.Request.QueryString}"
                        });
                }
                return null;
            }

            var reqCtx = new RequestHandlerContext
            {
                HttpContext = context,
                RavenServer = _ravenServer,
                RouteMatch = tryMatch.Match,
            };

            var tuple = tryMatch.Value.TryGetHandler(reqCtx);
            var handler = tuple.Item1 ?? await tuple.Item2;

            reqCtx.Database?.Metrics?.RequestsMeter.Mark();
            _serverMetrics.RequestsMeter.Mark();

            Interlocked.Increment(ref _serverMetrics.ConcurrentRequestsCount);
            if (handler == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                using (var ctx = JsonOperationContext.ShortTermSingleUse())
                using (var writer = new BlittableJsonTextWriter(ctx, context.Response.Body))
                {
                    ctx.Write(writer,
                        new DynamicJsonValue
                        {
                            ["Type"] = "Error",
                            ["Message"] = $"There is no handler for {context.Request.Method} {context.Request.Path}"
                        });
                }
                return null;
            }

            if (tryMatch.Value.NoAuthorizationRequired == false)
            {
                var authResult = TryAuthorize(context, _ravenServer.Configuration, reqCtx.Database);
                if (authResult == false)
                    return reqCtx.Database?.Name;
            }

            if (reqCtx.Database != null)
            {
                using (reqCtx.Database.DatabaseInUse(tryMatch.Value.SkipUsagesCount))
                    await handler(reqCtx);
            }
            else
            {
                await handler(reqCtx);
            }

            Interlocked.Decrement(ref _serverMetrics.ConcurrentRequestsCount);

            return reqCtx.Database?.Name;
        }

        private unsafe delegate int FromBase64_DecodeDelegate(char* startInputPtr, int inputLength, byte* startDestPtr, int destLength);

        static readonly FromBase64_DecodeDelegate _fromBase64_Decode = (FromBase64_DecodeDelegate)typeof(Convert).GetTypeInfo().GetMethod("FromBase64_Decode", BindingFlags.Static | BindingFlags.NonPublic)
            .CreateDelegate(typeof(FromBase64_DecodeDelegate));

        private bool TryAuthorize(HttpContext context, RavenConfiguration configuration,
            DocumentDatabase database)
        {
            var authHeaderValues = context.Request.Headers["Raven-Authorization"];
            var token = authHeaderValues.Count == 0 ? null : authHeaderValues[0];

            if (token == null)
            {
                token = context.Request.Cookies["Raven-Authorization"];
            }
            if (configuration.Server.AnonymousUserAccessMode == AnonymousUserAccessModeValues.Admin &&
                token == null)
                return true;
            var sigBase64Size = Sparrow.Utils.Base64.CalculateAndValidateOutputLength(Sodium.crypto_sign_bytes());
            if (token == null || token.Length < sigBase64Size + 8 /* sig length + prefix */)
            {
                context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                using (var ctx = JsonOperationContext.ShortTermSingleUse())
                using (var writer = new BlittableJsonTextWriter(ctx, context.Response.Body))
                {
                    DrainRequest(ctx, context);

                    ctx.Write(writer,
                        new DynamicJsonValue
                        {
                            ["Type"] = "Error",
                            ["Message"] = "The access token is required"
                        });
                }
                return false;
            }

            if (TryGetAccessToken(_ravenServer, token, sigBase64Size, out AccessToken accessToken) == false)
            {
                context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                using (var ctx = JsonOperationContext.ShortTermSingleUse())
                using (var writer = new BlittableJsonTextWriter(ctx, context.Response.Body))
                {
                    DrainRequest(ctx, context);

                    ctx.Write(writer,
                        new DynamicJsonValue
                        {
                            ["Type"] = "Error",
                            ["Message"] = "The access token is invalid or expired"
                        });
                }
                return false;
            }

            var resourceName = database?.Name;
            if (resourceName == null)
                return true;

            var hasValue = accessToken.AuthorizedDatabases.TryGetValue(resourceName, out AccessModes mode) ||
                           accessToken.AuthorizedDatabases.TryGetValue("*", out mode);
            if (hasValue == false)
                mode = AccessModes.None;

            switch (mode)
            {
                case AccessModes.None:
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    using (var ctx = JsonOperationContext.ShortTermSingleUse())
                    using (var writer = new BlittableJsonTextWriter(ctx, context.Response.Body))
                    {
                        DrainRequest(ctx, context);

                        ctx.Write(writer,
                            new DynamicJsonValue
                            {
                                ["Type"] = "Error",
                                ["Message"] = $"Api Key {accessToken.Name} does not have access to {resourceName}"
                            });
                    }
                    return false;
                case AccessModes.ReadOnly:
                    if (context.Request.Method != "GET")
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        using (var ctx = JsonOperationContext.ShortTermSingleUse())
                        using (var writer = new BlittableJsonTextWriter(ctx, context.Response.Body))
                        {
                            DrainRequest(ctx, context);

                            ctx.Write(writer,
                                new DynamicJsonValue
                                {
                                    ["Type"] = "Error",
                                    ["Message"] = $"Api Key {accessToken.Name} does not have write access to {resourceName} but made a {context.Request.Method} request"
                                });
                        }
                        return false;
                    }
                    return true;
                case AccessModes.ReadWrite:
                case AccessModes.Admin:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException("Unknown access mode: " + mode);
            }
        }

        public static unsafe bool TryGetAccessToken(RavenServer ravenServer, string token, int sigBase64Size, out AccessToken accessToken)
        {
            if (ravenServer.AccessTokenCache.TryGetValue(token, out accessToken) == false)
            {
                var tokenBytes = Encodings.Utf8.GetBytes(token);
                var signature = new byte[Sodium.crypto_sign_bytes()];
                fixed (byte* sig = signature)
                fixed (byte* msg = tokenBytes)
                fixed (char* pToken = token)
                {
                    _fromBase64_Decode(pToken + 8, sigBase64Size, sig, Sodium.crypto_sign_bytes());
                    Memory.Set(msg + 8, (byte)' ', sigBase64Size);

                    using (ravenServer.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext txContext))
                    using (txContext.OpenReadTransaction())
                    using (var tokenJson = txContext.ParseBuffer(msg, tokenBytes.Length, "auth token", BlittableJsonDocumentBuilder.UsageMode.None))
                    {

                        if (tokenJson.TryGet("Node", out string tag) == false)
                            throw new InvalidOperationException("Missing 'Node' property in authentication token");
                        if (tokenJson.TryGet("Name", out string apiKeyName) == false)
                            throw new InvalidOperationException("Missing 'Name' property in authentication token");
                        if (tokenJson.TryGet("Expires", out string expires) == false)
                            throw new InvalidOperationException("Missing 'Expires' property in authentication token");

                        var clusterTopology = ravenServer.ServerStore.GetClusterTopology(txContext);
                        var publicKey = clusterTopology.GetPublicKeyFromTag(tag);
                        //This is the case where we don't know the server but the admin had injected the server's 
                        //public key into our server store using an external tool.
                        var unknownPublicKey = false;
                        if (publicKey == null)
                        {
                            //If we are not a part of a cluster we won't have our public key in the topology so we will 
                            // try to validate against our own public key
                            publicKey = ServerStore.GetSecretKey(txContext, $"Raven/Sign/Public/{tag}");
                            if (publicKey == null)
                            {
                                unknownPublicKey = true;
                                publicKey = ServerStore.GetSecretKey(txContext, $"Raven/Sign/Public");
                            }
                            if (publicKey == null)
                            {
                                throw new InvalidOperationException("Unable to find any valid public key to validate the token");
                            }
                        }

                        if (publicKey.Length < 32)
                        {
                            throw new InvalidOperationException("Unable to find any valid public key for " + tag + " to validate the token");
                        }

                        fixed (byte* pk = publicKey)
                        {
                            if (Sodium.crypto_sign_verify_detached(sig, msg, (ulong)tokenBytes.Length, pk) != 0)
                            {
                                if (unknownPublicKey == false ||
                                    ravenServer.Configuration.Server.AnonymousUserAccessMode != AnonymousUserAccessModeValues.Admin)
                                    return false;

                                // if the key failed to validate, but we aren't familiar with the public key AND
                                // the user said that we should allow anonymous access, then we accept the token 
                                // as trusted. This is meant to simplify the deployment process of clusters in
                                // networks that are trusted only. When anonymous users don't have admin access,
                                // then we require the administrator to install the second server key first.
                            }
                        }

                        accessToken = new AccessToken
                        {
                            Token = token,
                            Name = apiKeyName,
                            Expires = DateTime.ParseExact(expires, "O", CultureInfo.InvariantCulture),
                            AuthorizedDatabases = GetAuthorizedDatabases(txContext, ravenServer, apiKeyName)
                        };

                        if (accessToken.IsExpired == false)
                            ravenServer.AccessTokenCache.TryAdd(token, accessToken);
                    }
                }

            }

            return accessToken.IsExpired == false;
        }

        public static Dictionary<string, AccessModes> GetAuthorizedDatabases(TransactionOperationContext txContext, RavenServer ravenServer, string apiKeyName)
        {
            if (apiKeyName.StartsWith("Raven"))
                return new Dictionary<string, AccessModes> {{"*", AccessModes.Admin}};

            var apiDoc = ravenServer.ServerStore.Cluster.Read(txContext, Constants.ApiKeys.Prefix + apiKeyName);
            if (apiDoc == null)
                throw new AuthenticationException($"Could not find api key: {apiKeyName}");

            if (apiDoc.TryGet("ResourcesAccessMode", out BlittableJsonReaderObject resourcesAccessMode) == false)
                throw new InvalidOperationException($"Missing 'ResourcesAccessMode' property in api key: {apiKeyName}");

            var prop = new BlittableJsonReaderObject.PropertyDetails();

            var databases = new Dictionary<string, AccessModes>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < resourcesAccessMode.Count; i++)
            {
                resourcesAccessMode.GetPropertyByIndex(i, ref prop);

                if (resourcesAccessMode.TryGet(prop.Name, out string accessMode) == false)
                {
                    throw new InvalidOperationException($"Missing value of dbName -'{prop.Name}' property in api key: {apiKeyName}");
                }
                if (Enum.TryParse(accessMode, out AccessModes value) == false)
                {
                    throw new InvalidOperationException(
                        $"Invalid value of dbName -'{prop.Name}' property in api key: {apiKeyName}, cannot understand: {accessMode}");
                }
                databases[prop.Name] = value;
            }
            return databases;
        }

        private void DrainRequest(JsonOperationContext ctx, HttpContext context)
        {
            if (context.Response.Headers.TryGetValue("Connection", out StringValues value) && value == "close")
                return; // don't need to drain it, the connection will close 

            using (ctx.GetManagedBuffer(out JsonOperationContext.ManagedPinnedBuffer buffer))
            {
                var requestBody = context.Request.Body;
                while (true)
                {
                    var read = requestBody.Read(buffer.Buffer.Array, buffer.Buffer.Offset, buffer.Buffer.Count);
                    if (read == 0)
                        break;
                }
            }
        }
    }
}