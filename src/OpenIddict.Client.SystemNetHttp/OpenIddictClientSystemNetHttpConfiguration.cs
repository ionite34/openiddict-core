﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Polly;

#if SUPPORTS_HTTP_CLIENT_RESILIENCE
using Microsoft.Extensions.Http.Resilience;
#endif

namespace OpenIddict.Client.SystemNetHttp;

/// <summary>
/// Contains the methods required to ensure that the OpenIddict client/System.Net.Http integration configuration is valid.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class OpenIddictClientSystemNetHttpConfiguration : IConfigureOptions<OpenIddictClientOptions>,
                                                                 IConfigureNamedOptions<HttpClientFactoryOptions>,
                                                                 IPostConfigureOptions<HttpClientFactoryOptions>,
                                                                 IPostConfigureOptions<OpenIddictClientSystemNetHttpOptions>
{
    private readonly IServiceProvider _provider;

    /// <summary>
    /// Creates a new instance of the <see cref="OpenIddictClientSystemNetHttpConfiguration"/> class.
    /// </summary>
    /// <param name="provider">The service provider.</param>
    public OpenIddictClientSystemNetHttpConfiguration(IServiceProvider provider)
        => _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <inheritdoc/>
    public void Configure(OpenIddictClientOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // Register the built-in event handlers used by the OpenIddict System.Net.Http client components.
        options.Handlers.AddRange(OpenIddictClientSystemNetHttpHandlers.DefaultHandlers);

        // Enable client_secret_basic, self_signed_tls_client_auth and tls_client_auth support by default.
        options.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.ClientSecretBasic);
        options.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.SelfSignedTlsClientAuth);
        options.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.TlsClientAuth);
    }

    /// <inheritdoc/>
    public void Configure(HttpClientFactoryOptions options) => Configure(Options.DefaultName, options);

    /// <inheritdoc/>
    public void Configure(string? name, HttpClientFactoryOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var assembly = typeof(OpenIddictClientSystemNetHttpOptions).Assembly.GetName();

        // Only amend the HTTP client factory options if the instance is managed by OpenIddict.
        if (string.IsNullOrEmpty(name) || !name.StartsWith(assembly.Name!, StringComparison.Ordinal))
        {
            return;
        }

        // Note: HttpClientFactory doesn't support flowing a list of properties that can be
        // accessed from the HttpClientAction or HttpMessageHandlerBuilderAction delegates
        // to dynamically amend the resulting HttpClient or HttpClientHandler instance.
        //
        // To work around this limitation, the OpenIddict System.Net.Http integration uses
        // dynamic client names and supports appending a list of key-value pairs to the client
        // name to flow per-instance properties (e.g the negotiated client authentication method).
        var properties = name.Length >= assembly.Name!.Length + 1 && name[assembly.Name.Length] is ':' ?
            name[(assembly.Name.Length + 1)..]
                .Split(['\u001f'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static property => property.Split(['\u001e'], StringSplitOptions.RemoveEmptyEntries))
                .Where(static values => values is [{ Length: > 0 }, { Length: > 0 }])
                .ToDictionary(static values => values[0], static values => values[1]) : [];

        if (!properties.TryGetValue("RegistrationId", out string? identifier) || string.IsNullOrEmpty(identifier))
        {
            return;
        }

        var service = _provider.GetRequiredService<OpenIddictClientService>();

        // Note: while the client registration should be returned synchronously in most cases,
        // the retrieval is always offloaded to the thread pool to help prevent deadlocks when
        // the waiting is blocking and the operation is executed in a synchronization context.
        var registration = Task.Run(async () => await service.GetClientRegistrationByIdAsync(identifier)).GetAwaiter().GetResult();

        var settings = _provider.GetRequiredService<IOptionsMonitor<OpenIddictClientSystemNetHttpOptions>>().CurrentValue;

        options.HttpClientActions.Add(static client =>
        {
            // By default, HttpClient uses a default timeout of 100 seconds and allows payloads of up to 2GB.
            // To help reduce the effects of malicious responses (e.g responses returned at a very slow pace
            // or containing an infine amount of data), the default values are amended to use lower values.
            client.MaxResponseContentBufferSize = 10 * 1024 * 1024;
            client.Timeout = TimeSpan.FromMinutes(1);
        });

        // Register the user-defined HTTP client actions.
        foreach (var action in settings.HttpClientActions)
        {
            options.HttpClientActions.Add(client => action(registration, client));
        }

        options.HttpMessageHandlerBuilderActions.Add(builder =>
        {
#if SUPPORTS_SERVICE_PROVIDER_IN_HTTP_MESSAGE_HANDLER_BUILDER
            var options = builder.Services.GetRequiredService<IOptionsMonitor<OpenIddictClientSystemNetHttpOptions>>();
#else
            var options = _provider.GetRequiredService<IOptionsMonitor<OpenIddictClientSystemNetHttpOptions>>();
#endif
            // If applicable, add the handler responsible for replaying failed HTTP requests.
            //
            // Note: on .NET 8.0 and higher, the HTTP error policy is always set
            // to null by default and an HTTP resilience pipeline is used instead.
            if (options.CurrentValue.HttpErrorPolicy is IAsyncPolicy<HttpResponseMessage> policy)
            {
                builder.AdditionalHandlers.Add(new PolicyHttpMessageHandler(policy));
            }

#if SUPPORTS_HTTP_CLIENT_RESILIENCE
            else if (options.CurrentValue.HttpResiliencePipeline is ResiliencePipeline<HttpResponseMessage> pipeline)
            {
#pragma warning disable EXTEXP0001
                builder.AdditionalHandlers.Add(new ResilienceHandler(pipeline));
#pragma warning restore EXTEXP0001
            }
#endif
            if (builder.PrimaryHandler is not HttpClientHandler handler)
            {
                throw new InvalidOperationException(SR.FormatID0373(typeof(HttpClientHandler).FullName));
            }

            handler.ClientCertificateOptions = ClientCertificateOption.Manual;

            if (properties.TryGetValue("AttachTlsClientCertificate", out string? value) &&
                bool.TryParse(value, out bool result) && result)
            {
                var certificate = options.CurrentValue.TlsClientAuthenticationCertificateSelector(registration);
                if (certificate is not null)
                {
                    handler.ClientCertificates.Add(certificate);
                }
            }

            else if (properties.TryGetValue("AttachSelfSignedTlsClientCertificate", out value) &&
                bool.TryParse(value, out result) && result)
            {
                var certificate = options.CurrentValue.SelfSignedTlsClientAuthenticationCertificateSelector(registration);
                if (certificate is not null)
                {
                    handler.ClientCertificates.Add(certificate);
                }
            }
        });

        // Register the user-defined HTTP client handler actions.
        foreach (var action in settings.HttpClientHandlerActions)
        {
            options.HttpMessageHandlerBuilderActions.Add(builder => action(registration,
                builder.PrimaryHandler as HttpClientHandler ??
                    throw new InvalidOperationException(SR.FormatID0373(typeof(HttpClientHandler).FullName))));
        }
    }

    /// <inheritdoc/>
    public void PostConfigure(string? name, HttpClientFactoryOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var assembly = typeof(OpenIddictClientSystemNetHttpOptions).Assembly.GetName();

        // Only amend the HTTP client factory options if the instance is managed by OpenIddict.
        if (string.IsNullOrEmpty(name) || !name.StartsWith(assembly.Name!, StringComparison.Ordinal))
        {
            return;
        }

        // Note: HttpClientFactory doesn't support flowing a list of properties that can be
        // accessed from the HttpClientAction or HttpMessageHandlerBuilderAction delegates
        // to dynamically amend the resulting HttpClient or HttpClientHandler instance.
        //
        // To work around this limitation, the OpenIddict System.Net.Http integration uses dynamic
        // client names and supports appending a list of key-value pairs to the client name to flow
        // per-instance properties (e.g a flag indicating whether a client certificate should be used).
        var properties = name.Length >= assembly.Name!.Length + 1 && name[assembly.Name.Length] is ':' ?
            name[(assembly.Name.Length + 1)..]
                .Split(['\u001f'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static property => property.Split(['\u001e'], StringSplitOptions.RemoveEmptyEntries))
                .Where(static values => values is [{ Length: > 0 }, { Length: > 0 }])
                .ToDictionary(static values => values[0], static values => values[1]) : [];

        if (!properties.TryGetValue("RegistrationId", out string? identifier) || string.IsNullOrEmpty(identifier))
        {
            return;
        }

        options.HttpMessageHandlerBuilderActions.Insert(0, static builder =>
        {
            // Note: Microsoft.Extensions.Http 9.0+ no longer uses HttpClientHandler as the default instance
            // for PrimaryHandler on platforms that support SocketsHttpHandler. Since OpenIddict requires an
            // HttpClientHandler instance, it is manually reassigned here if it's not an HttpClientHandler.
            if (builder.PrimaryHandler is not HttpClientHandler)
            {
                builder.PrimaryHandler = new HttpClientHandler();
            }
        });

        options.HttpMessageHandlerBuilderActions.Add(static builder =>
        {
            if (builder.PrimaryHandler is not HttpClientHandler handler)
            {
                throw new InvalidOperationException(SR.FormatID0373(typeof(HttpClientHandler).FullName));
            }

            // Note: automatic content decompression can be enabled by constructing an HttpClient wrapping
            // a generic HttpClientHandler, a SocketsHttpHandler or a WinHttpHandler instance with the
            // AutomaticDecompression property set to the desired algorithms (e.g GZip, Deflate or Brotli).
            //
            // Unfortunately, while convenient and efficient, relying on this property has a downside:
            // setting AutomaticDecompression always overrides the Accept-Encoding header of all requests
            // to include the selected algorithms without offering a way to make this behavior opt-in.
            // Sadly, using HTTP content compression with transport security enabled has security implications
            // that could potentially lead to compression side-channel attacks if the client is used with
            // remote endpoints that reflect user-defined data and contain secret values (e.g BREACH attacks).
            //
            // Since OpenIddict itself cannot safely assume such scenarios will never happen (e.g a token request
            // will typically be sent with an authorization code that can be defined by a malicious user and can
            // potentially be reflected in the token response depending on the configuration of the remote server),
            // it is safer to disable compression by default by not sending an Accept-Encoding header while
            // still allowing encoded responses to be processed (e.g StackExchange forces content compression
            // for all the supported HTTP APIs even if no Accept-Encoding header is explicitly sent by the client).
            //
            // For these reasons, OpenIddict doesn't rely on the automatic decompression feature and uses
            // a custom event handler to deal with GZip/Deflate/Brotli-encoded responses, so that servers
            // that require using HTTP compression can be supported without having to use it for all servers.
            if (handler.SupportsAutomaticDecompression)
            {
                handler.AutomaticDecompression = DecompressionMethods.None;
            }

            // OpenIddict uses IHttpClientFactory to manage the creation of the HTTP clients and
            // their underlying HTTP message handlers, that are cached for the specified duration
            // and re-used to process multiple requests during that period. While remote APIs are
            // typically not expected to return cookies, it is in practice a very frequent case,
            // which poses a serious security issue when the cookies are shared across multiple
            // requests (which is the case when the same message handler is cached and re-used).
            //
            // To avoid that, cookies support is explicitly disabled here, for security reasons.
            handler.UseCookies = false;
        });
    }

    /// <inheritdoc/>
    public void PostConfigure(string? name, OpenIddictClientSystemNetHttpOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // If no client authentication certificate selector was provided, use fallback delegates that
        // automatically use the first X.509 signing certificate attached to the client registration
        // that is suitable for both digital signature and client authentication.

        options.SelfSignedTlsClientAuthenticationCertificateSelector ??= static registration =>
        {
            foreach (var credentials in registration.SigningCredentials)
            {
                if (credentials.Key is X509SecurityKey { Certificate: X509Certificate2 certificate } &&
                    certificate.Version is >= 3 && IsSelfIssuedCertificate(certificate) &&
                    HasDigitalSignatureKeyUsage(certificate) &&
                    HasClientAuthenticationExtendedKeyUsage(certificate))
                {
                    return certificate;
                }
            }

            return null;
        };

        options.TlsClientAuthenticationCertificateSelector ??= static registration =>
        {
            foreach (var credentials in registration.SigningCredentials)
            {
                if (credentials.Key is X509SecurityKey { Certificate: X509Certificate2 certificate } &&
                    certificate.Version is >= 3 && !IsSelfIssuedCertificate(certificate) &&
                    HasDigitalSignatureKeyUsage(certificate) &&
                    HasClientAuthenticationExtendedKeyUsage(certificate))
                {
                    return certificate;
                }
            }

            return null;
        };

        static bool HasClientAuthenticationExtendedKeyUsage(X509Certificate2 certificate)
        {
            for (var index = 0; index < certificate.Extensions.Count; index++)
            {
                if (certificate.Extensions[index] is X509EnhancedKeyUsageExtension extension &&
                    HasOid(extension.EnhancedKeyUsages, "1.3.6.1.5.5.7.3.2"))
                {
                    return true;
                }
            }

            return false;

            static bool HasOid(OidCollection collection, string value)
            {
                for (var index = 0; index < collection.Count; index++)
                {
                    if (collection[index] is Oid oid && string.Equals(oid.Value, value, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        static bool HasDigitalSignatureKeyUsage(X509Certificate2 certificate)
        {
            for (var index = 0; index < certificate.Extensions.Count; index++)
            {
                if (certificate.Extensions[index] is X509KeyUsageExtension extension &&
                    extension.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
                {
                    return true;
                }
            }

            return false;
        }

        // Note: to avoid building and introspecting a X.509 certificate chain and reduce the cost
        // of this check, a certificate is always assumed to be self-signed when it is self-issued.
        static bool IsSelfIssuedCertificate(X509Certificate2 certificate)
            => certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
    }
}
