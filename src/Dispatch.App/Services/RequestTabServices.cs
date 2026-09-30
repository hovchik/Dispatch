using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Infrastructure.Protocols;
using Dispatch.Infrastructure.Protocols.Grpc;

namespace Dispatch.App.Services;

/// <summary>Everything a request tab needs from the app, in one injectable bundle.</summary>
public sealed record RequestTabServices(
    IRequestSender Sender,
    ICollectionRepository Collections,
    IClipboardService Clipboard,
    IDialogService Dialogs,
    GraphQlExecutor GraphQl,
    GrpcSchemaProvider GrpcSchemas,
    WsdlLoader Wsdl);
