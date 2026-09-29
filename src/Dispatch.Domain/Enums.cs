namespace Dispatch.Domain;

public enum HttpVerb
{
    Get,
    Post,
    Put,
    Patch,
    Delete,
    Head,
    Options
}

public enum BodyMode
{
    None,
    Json,
    Text,
    Xml,
    FormUrlEncoded
}

public enum AuthMode
{
    None,
    Bearer,
    Basic,
    ApiKey
}

public enum ApiKeyLocation
{
    Header,
    QueryParam
}
