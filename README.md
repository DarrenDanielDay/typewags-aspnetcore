# typewags-aspnetcore

The server side Web API inspector for ASP.NET Core projects.

To generate TypeScript client definitions, see [typewags](https://github.com/DarrenDanielDay/typewags).

## Usage

```cs
// In StartUp.Configure
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapGet("typewags", async (context) =>
        {
            // This package just reflects the assembly and collects the `controller`s in the given assembly.
            // The controller methods are resolved as web APIs.
            var inspectResult = DarrenDanielDay.Typeawags.AspNetCoreWebAPIInspector.AllInOne(typeof(Program).Assembly);
            // Please use Newtonsoft.Json, not System.Text.Json.
            // System.Text.Json cannot serilize the InspectResult to json correctly.
            // You can also just return InspectResult in a controller's method.
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(inspectResult);
            await context.Response.WriteAsync(json, System.Text.Encoding.UTF8);
        });
});
```