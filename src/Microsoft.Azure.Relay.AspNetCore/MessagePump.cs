// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics.Contracts;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.Relay.AspNetCore
{
    internal class MessagePump : IServer
    {
        private readonly ILogger _logger;
        private readonly AzureRelayOptions _options;

        private IHttpApplication<object> _application;

        private Action<object> _processRequest;

        private volatile int _stopping;
        private int _outstandingRequests;
        private readonly TaskCompletionSource<object> _shutdownSignal = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _shutdownSignalCompleted;

        private readonly ServerAddressesFeature _serverAddresses;

        public MessagePump(IOptions<AzureRelayOptions> options, ILoggerFactory loggerFactory, IAuthenticationSchemeProvider authentication)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _options = options.Value;
            Listener = new AzureRelayListener(_options, loggerFactory, ProcessRequest);
            _logger = LogHelper.CreateLogger(loggerFactory, typeof(MessagePump));

            Features = new FeatureCollection();
            _serverAddresses = new ServerAddressesFeature();
            Features.Set<IServerAddressesFeature>(_serverAddresses);

            _processRequest = new Action<object>(ProcessRequestAsync);
        }

        internal AzureRelayListener Listener { get; }

        public IFeatureCollection Features { get; }

        private bool Stopping => _stopping == 1;

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            var hostingUrlsPresent = _serverAddresses.Addresses.Count > 0;

            if (_serverAddresses.PreferHostingUrls && hostingUrlsPresent)
            {
                if (_options.UrlPrefixes.Count > 0)
                {
                    LogHelper.LogWarning(_logger, $"Overriding endpoints added to {nameof(AzureRelayOptions.UrlPrefixes)} since {nameof(IServerAddressesFeature.PreferHostingUrls)} is set to true." +
                        $" Binding to address(es) '{string.Join(", ", _serverAddresses.Addresses)}' instead. ");

                    Listener.Options.UrlPrefixes.Clear();
                }

                foreach (var value in _serverAddresses.Addresses)
                {
                    Listener.Options.UrlPrefixes.Add(UrlPrefix.Create(value));
                }
            }
            else if (_options.UrlPrefixes.Count > 0)
            {
                if (hostingUrlsPresent)
                {
                    LogHelper.LogWarning(_logger, $"Overriding address(es) '{string.Join(", ", _serverAddresses.Addresses)}'. " +
                        $"Binding to endpoints added to {nameof(AzureRelayOptions.UrlPrefixes)} instead.");

                    _serverAddresses.Addresses.Clear();
                }

                foreach (var prefix in _options.UrlPrefixes)
                {
                    _serverAddresses.Addresses.Add(prefix.FullPrefix);
                }
            }
            else if (hostingUrlsPresent)
            {
                foreach (var value in _serverAddresses.Addresses)
                {
                    Listener.Options.UrlPrefixes.Add(UrlPrefix.Create(value));
                }
            }
            else
            {
                LogHelper.LogDebug(_logger, $"No listening endpoints were configured.");
                throw new InvalidOperationException("No listening endpoints were configured.");
            }

            // Can't call Start twice
            Contract.Assert(_application == null);
            Contract.Assert(application != null);

            _application = new ApplicationWrapper<TContext>(application);

            Listener.Start();

            return Task.CompletedTask;
        }

        // The message pump.
        // When we start listening for the next request on one thread, we may need to be sure that the
        // completion continues on another thread as to not block the current request processing.
        // The awaits will manage stack depth for us.
        private void ProcessRequest(RequestContext context)
        {
            try
            {
                Task ignored = Task.Factory.StartNew(_processRequest, context);
            }
            catch (Exception ex)
            {
                // Request processing failed to be queued in threadpool
                // Log the error message, release throttle and move on
                LogHelper.LogException(_logger, nameof(ProcessRequest), ex);
            }
        }

        private async void ProcessRequestAsync(object requestContextObj)
        {
            try
            {
                var requestContext = (RequestContext)requestContextObj;
                if (Stopping)
                {
                    SetFatalResponse(requestContext, (HttpStatusCode)503);
                    return;
                }

                object context = null;
                Interlocked.Increment(ref _outstandingRequests);
                try
                {
                    var featureContext = new FeatureContext(requestContext);
                    context = _application.CreateContext(featureContext.Features);

                    try
                    {
                        await _application.ProcessRequestAsync(context);
                        await featureContext.OnResponseStart();
                    }
                    finally
                    {
                        await featureContext.OnCompleted();
                    }

                    _application.DisposeContext(context, null);
                }
                catch (Exception ex)
                {
                    LogHelper.LogException(_logger, nameof(ProcessRequestAsync), ex);
                    _application.DisposeContext(context, ex);
                        // We haven't sent a response yet, try to send a 500 Internal Server Error
                        requestContext.Response.Headers.Clear();
                        SetFatalResponse(requestContext, (HttpStatusCode)500);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _outstandingRequests) == 0 && Stopping)
                    {
                        LogHelper.LogInfo(_logger, "All requests drained.");
                        _shutdownSignal.TrySetResult(0);
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogException(_logger, nameof(ProcessRequestAsync), ex);
            }
        }

        private static void SetFatalResponse(RequestContext context, HttpStatusCode status)
        {
            context.Response.StatusCode = (int)status;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            void RegisterCancelation()
            {
                cancellationToken.Register(() =>
                {
                    if (Interlocked.Exchange(ref _shutdownSignalCompleted, 1) == 0)
                    {
                        LogHelper.LogInfo(_logger, "Canceled, terminating " + _outstandingRequests + " request(s).");
                        _shutdownSignal.TrySetResult(null);
                    }
                });
            }

            if (Interlocked.Exchange(ref _stopping, 1) == 1)
            {
                RegisterCancelation();

                return _shutdownSignal.Task;
            }

            try
            {
                // Wait for active requests to drain
                if (_outstandingRequests > 0)
                {
                    LogHelper.LogInfo(_logger, "Stopping, waiting for " + _outstandingRequests + " request(s) to drain.");
                    RegisterCancelation();
                }
                else
                {
                    _shutdownSignal.TrySetResult(null);
                }
            }
            catch (Exception ex)
            {
                _shutdownSignal.TrySetException(ex);
            }

            return _shutdownSignal.Task;
        }

        public void Dispose()
        {
            _stopping = 1;
            _shutdownSignal.TrySetResult(null);

            Listener.Dispose();
        }

        private class ApplicationWrapper<TContext> : IHttpApplication<object>
        {
            private readonly IHttpApplication<TContext> _application;

            public ApplicationWrapper(IHttpApplication<TContext> application)
            {
                _application = application;
            }

            public object CreateContext(IFeatureCollection contextFeatures)
            {
                return _application.CreateContext(contextFeatures);
            }

            public void DisposeContext(object context, Exception exception)
            {
                _application.DisposeContext((TContext)context, exception);
            }

            public Task ProcessRequestAsync(object context)
            {
                return _application.ProcessRequestAsync((TContext)context);
            }
        }
    }
}
