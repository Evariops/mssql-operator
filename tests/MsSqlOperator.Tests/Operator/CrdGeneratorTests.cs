using FluentAssertions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Tests.Operator;

public class CrdGeneratorTests
{
    // Regression guard: the entity graph (incl. embedded k8s types V1PodSecurityContext /
    // V1Toleration) used to stack-overflow KubeOps' transpiler. Our depth/cycle-guarded
    // generator must terminate and produce a valid CRD.
    [Theory]
    [InlineData(typeof(SqlInstance), "sqlinstances.mssql-operator.evariops.com", "sqlinstances")]
    [InlineData(typeof(SqlAvailabilityGroup), "sqlavailabilitygroups.mssql-operator.evariops.com", "sqlavailabilitygroups")]
    [InlineData(typeof(SqlTdeCertificate), "sqltdecertificates.mssql-operator.evariops.com", "sqltdecertificates")]
    public void Generate_ProducesValidCrd(Type entity, string expectedName, string expectedPlural)
    {
        var crd = CrdGenerator.Generate(entity);

        crd.Metadata.Name.Should().Be(expectedName);
        crd.Spec.Names.Plural.Should().Be(expectedPlural);
        crd.Spec.Scope.Should().Be("Namespaced");

        var version = crd.Spec.Versions.Single();
        version.Served.Should().BeTrue();
        version.Storage.Should().BeTrue();
        version.Subresources.Status.Should().NotBeNull();

        var schema = version.Schema.OpenAPIV3Schema;
        schema.Type.Should().Be("object");
        schema.Properties.Should().ContainKey("spec").And.ContainKey("status");
    }

    [Fact]
    public void Generate_ShortNames_ArePropagated()
    {
        var crd = CrdGenerator.Generate(typeof(SqlAvailabilityGroupDatabase));
        crd.Spec.Names.ShortNames.Should().Contain("sagdb");
    }
}
