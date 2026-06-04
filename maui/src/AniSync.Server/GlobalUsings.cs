// Original global usings carried over from the ASP.NET app's GlobalUsings.cs.
global using static Newtonsoft.Json.JsonConvert;
global using static AnimeList.Utils;
global using static AnimeList.Enumerations;

// The server source was written under the Microsoft.NET.Sdk.Web SDK, which adds a
// set of ASP.NET Core namespaces to the implicit global usings. This library uses
// the plain Microsoft.NET.Sdk (it's referenced by the Blazor Web head, which is the
// actual host), so those namespaces are re-declared here to keep the copied
// controllers/services compiling unchanged.
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Hosting;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
global using System.Net.Http.Json;
