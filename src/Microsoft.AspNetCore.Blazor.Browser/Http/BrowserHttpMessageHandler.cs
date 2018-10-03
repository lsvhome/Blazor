// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.JSInterop;
using Mono.WebAssembly.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Blazor.Browser.Http
{
    /// <summary>
    /// A browser-compatible implementation of <see cref="HttpMessageHandler"/>
    /// </summary>
    public class BrowserHttpMessageHandler : HttpMessageHandler
    {
        /// <summary>
        /// Gets or sets the default value of the 'credentials' option on outbound HTTP requests.
        /// Defaults to <see cref="FetchCredentialsOption.SameOrigin"/>.
        /// </summary>
        public static FetchCredentialsOption DefaultCredentials { get; set; }
            = FetchCredentialsOption.SameOrigin;

        static object _idLock = new object();
        static int _nextRequestId = 0;
        static IDictionary<int, TaskCompletionSource<HttpResponseMessage>> _pendingRequests
            = new Dictionary<int, TaskCompletionSource<HttpResponseMessage>>();

        /// <summary>
        /// The name of a well-known property that can be added to <see cref="HttpRequestMessage.Properties"/>
        /// to control the arguments passed to the underlying JavaScript <code>fetch</code> API.
        /// </summary>
        public const string FetchArgs = "BrowserHttpMessageHandler.FetchArgs";

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 001");
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 002");
            cancellationToken.Register(() => tcs.TrySetCanceled());
            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 003");

            int id;
            lock (_idLock)
            {
                System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 004");
                id = _nextRequestId++;
                System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 005");
                _pendingRequests.Add(id, tcs);
                System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 006");
            }

            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 007");
            var options = new FetchOptions();
            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 008");
            if (request.Properties.TryGetValue(FetchArgs, out var fetchArgs))
            {
                System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 009");
                options.RequestInitOverrides = fetchArgs;
            }

            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 010");
            options.RequestInit = new RequestInit
            {
                Credentials = GetDefaultCredentialsString(),
                Headers = GetHeadersAsStringArray(request),
                Method = request.Method.Method
            };

            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 011");
            options.RequestUri = request.RequestUri.ToString();
            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 012");
            var cnt = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync();
            if (JSRuntime.Current is MonoWebAssemblyJSRuntime mono)
            {

                //// !!!! var Http_1 = __webpack_require__(/*! ./Services/Http */ "./src/Services/Http.ts");
                System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 013 {cnt} {options}");
                var x = mono.InvokeUnmarshalled<int, byte[], string, object>(
                    "Blazor._internal.http.sendAsync",
                    id,
                    cnt,
                    Json.Serialize(options));

                System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 014 {x}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 015");
                throw new NotImplementedException("BrowserHttpMessageHandler only supports running under Mono WebAssembly.");
            }

            System.Diagnostics.Debug.WriteLine($"BrowserHttpMessageHandler.SendAsync 016 {tcs.Task.Status}");
            
            return await tcs.Task;
        }

        private string[][] GetHeadersAsStringArray(HttpRequestMessage request)
            => (from header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                from headerValue in header.Value // There can be more than one value for each name
                select new[] { header.Key, headerValue }).ToArray();

        private static void ReceiveResponse(
            string id,
            string responseDescriptorJson,
            byte[] responseBodyData,
            string errorText)
        {
            TaskCompletionSource<HttpResponseMessage> tcs;
            var idVal = int.Parse(id);
            lock (_idLock)
            {
                tcs = _pendingRequests[idVal];
                _pendingRequests.Remove(idVal);
            }

            if (errorText != null)
            {
                tcs.SetException(new HttpRequestException(errorText));
            }
            else
            {
                var responseDescriptor = Json.Deserialize<ResponseDescriptor>(responseDescriptorJson);
                var responseContent = responseBodyData == null ? null : new ByteArrayContent(responseBodyData);
                var responseMessage = responseDescriptor.ToResponseMessage(responseContent);
                tcs.SetResult(responseMessage);
            }
        }

        private static byte[] AllocateArray(string length)
        {
            return new byte[int.Parse(length)];
        }

        private static string GetDefaultCredentialsString()
        {
            // See https://developer.mozilla.org/en-US/docs/Web/API/Request/credentials for
            // standard values and meanings
            switch (DefaultCredentials)
            {
                case FetchCredentialsOption.Omit:
                    return "omit";
                case FetchCredentialsOption.SameOrigin:
                    return "same-origin";
                case FetchCredentialsOption.Include:
                    return "include";
                default:
                    throw new ArgumentException($"Unknown credentials option '{DefaultCredentials}'.");
            }
        }

        // Keep these in sync with TypeScript class in Http.ts
        private class FetchOptions
        {
            public string RequestUri { get; set; }
            public RequestInit RequestInit { get; set; }
            public object RequestInitOverrides { get; set; }
        }

        private class RequestInit
        {
            public string Credentials { get; set; }
            public string[][] Headers { get; set; }
            public string Method { get; set; }
        }

        private class ResponseDescriptor
        {
            #pragma warning disable 0649
            public int StatusCode { get; set; }
            public string StatusText { get; set; }
            public string[][] Headers { get; set; }
            #pragma warning restore 0649

            public HttpResponseMessage ToResponseMessage(HttpContent content)
            {
                var result = new HttpResponseMessage((HttpStatusCode)StatusCode);
                result.ReasonPhrase = StatusText;
                result.Content = content;
                var headers = result.Headers;
                var contentHeaders = result.Content?.Headers;
                foreach (var pair in Headers)
                {
                    if (!headers.TryAddWithoutValidation(pair[0], pair[1]))
                    {
                        contentHeaders?.TryAddWithoutValidation(pair[0], pair[1]);
                    }
                }

                return result;
            }
        }
    }
}
