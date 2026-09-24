var builder = DistributedApplication.CreateBuilder(args);

const int ConsumerCount = 5;

var database = builder.AddPostgres("postgres")
    .AddDatabase("dispatchly");

var shipping = builder.AddProject<Projects.Shipping>("shipping")
    .WithReference(database)
    .WaitFor(database)
    .WithReplicas(ConsumerCount);

builder.AddProject<Projects.Checkout>("checkout")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(shipping)
    .WithEnvironment("Checkout__PublishesPerSecond", ConsumerCount.ToString());

builder.Build().Run();
