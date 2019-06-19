// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.Azure.Relay.AspNetCore
{
    public class AzureRelayOptions
    {
        public AzureRelayOptions()
        {
        }

        public TokenProvider TokenProvider { get; set; }

        public UrlPrefixCollection UrlPrefixes { get; } = new UrlPrefixCollection();

        /// <summary>
        /// Raised when the Listener is attempting to reconnect with ServiceBus after a connection loss.
        /// </summary>
        public EventHandler Connecting;

        /// <summary>
        /// Raised when the Listener has successfully connected with ServiceBus
        /// </summary>
        public EventHandler Online;

        /// <summary>
        /// Raised when the Listener will no longer be attempting to (re)connect with ServiceBus.
        /// </summary>
        public EventHandler Offline;

        internal bool ThrowWriteExceptions { get; set; }

        internal long MaxRequestBodySize { get; set; }

        internal int RequestQueueLimit { get; set; }

        internal int? MaxConnections { get; set; }        
    }
}
