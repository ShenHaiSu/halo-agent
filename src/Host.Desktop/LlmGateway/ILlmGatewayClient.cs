using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.LlmGateway;

public interface ILlmGatewayClient
{
    IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
        UnifiedLlmRequest request,
        CancellationToken ct = default
    );
}
