var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres")
    .AddDatabase("dispatchly");

var shipping = builder.AddProject<Projects.Shipping>("shipping")
    .WithReference(database)
    .WaitFor(database);

builder.AddProject<Projects.Checkout>("checkout")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(shipping);

builder.Build().Run();
