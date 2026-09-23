using Dispatchly.Abstractions;
using Dispatchly.Core;
using Dispatchly.DependencyInjection;
using Dispatchly.SourceGenerators;
using Dispatchly.Transport.InMemory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dispatchly.SourceGenerators.Tests;

public class DispatchlyGeneratorTests
{
    [Fact]
    public async Task GeneratedRegistration_DeliversWithoutReflection()
    {
        var probe = new GeneratedOrderProbe();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(probe);
        builder.Services.AddDispatchly()
            .AddDispatchlyGeneratedHandlers()
            .UseInMemoryTransport(options => options.RetryDelay = TimeSpan.Zero);
        using var host = builder.Build();
        await host.StartAsync();
        var publisher = host.Services.GetRequiredService<IMessagePublisher>();
        await publisher.PublishAsync(new GeneratedOrder("A-1", 2));
        var delivered = await probe.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("A-1", delivered.OrderId);
        Assert.Equal(2, delivered.Quantity);
        await host.StopAsync();
    }

    [Fact]
    public void Emit_WritesHandlerRegistrationAndJsonContext()
    {
        var generated = Generate("""
            using Dispatchly.Abstractions;
            namespace Sample;
            [DispatchlyTable("placed_orders")]
            public sealed record OrderPlaced(string OrderId);
            public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
            {
                public System.Threading.Tasks.Task HandleAsync(OrderPlaced message, MessageContext context, System.Threading.CancellationToken cancellationToken)
                    => System.Threading.Tasks.Task.CompletedTask;
            }
            """);
        Assert.Contains("AddDispatchlyGeneratedHandlers", generated, StringComparison.Ordinal);
        Assert.Contains("DispatchlyJsonContext", generated, StringComparison.Ordinal);
        Assert.Contains("\"placed_orders\"", generated, StringComparison.Ordinal);
        Assert.Contains("OrderPlacedHandler", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_OmitsTableNameWhenTheAttributeIsAbsent()
    {
        var generated = Generate("""
            using Dispatchly.Abstractions;
            namespace Sample;
            public sealed record OrderPlaced(string OrderId);
            public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
            {
                public System.Threading.Tasks.Task HandleAsync(OrderPlaced message, MessageContext context, System.Threading.CancellationToken cancellationToken)
                    => System.Threading.Tasks.Task.CompletedTask;
            }
            """);
        Assert.Contains("AddHandler<global::Sample.OrderPlaced, global::Sample.OrderPlacedHandler>(global::Dispatchly.Generated.DispatchlyJsonContext.Default.OrderPlaced);", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("\"order_placed\"", generated, StringComparison.Ordinal);
    }

    private static string Generate(string source)
    {
        var syntax = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Concat([MetadataReference.CreateFromFile(typeof(IMessageHandler<>).Assembly.Location)]);
        var compilation = CSharpCompilation.Create(
            "Sample",
            [syntax],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new DispatchlyGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Empty(errors);
        var generated = string.Join(
            "\n",
            updated.SyntaxTrees.Select(tree => tree.ToString()));
        var emit = Emit(updated);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return generated;
    }

    private static Microsoft.CodeAnalysis.Emit.EmitResult Emit(Compilation compilation) =>
        compilation.AddReferences(
            MetadataReference.CreateFromFile(typeof(DispatchlyBuilder).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MessageDispatcher).Assembly.Location))
            .Emit(Stream.Null);
}
