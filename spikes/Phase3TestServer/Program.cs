using Phase3TestSupport;

var server = Phase3TestServer.Start(5201, 5233, 5234);
Console.WriteLine("Phase3 test server: HTTP 5201 / HTTPS 5233 / proxy 5234");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();
await server.DisposeAsync();
