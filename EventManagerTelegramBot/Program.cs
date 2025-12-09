using EventManagerTelegramBot;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("MySql");
builder.Services.AddSingleton(new MySqlConnection(connectionString));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
