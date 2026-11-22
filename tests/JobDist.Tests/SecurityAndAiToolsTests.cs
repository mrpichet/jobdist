using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobDist.Contracts;
using JobDist.Contracts.Security;
using JobDist.Core.Domain;
using JobDist.Core.Options;
using JobDist.Core.Security;
using JobDist.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobDist.Tests;

/// <summary>Cryptography (hybrid PQC), input validation and the Microsoft.Extensions.AI tools.</summary>
[TestClass]
public sealed class SecurityAndAiToolsTests
{
    [TestMethod]
    public void Hybrid_envelope_round_trips_and_rejects_tampering()
    {
        using var keys = HybridKeyPair.Generate();
        var aad = Guid.NewGuid().ToByteArray();
        var env = PqcEnvelope.Seal(keys.EcdhPublicKeyBase64, keys.MlKemPublicKeyBase64, "hello"u8, aad);

        Assert.AreEqual("hello", Encoding.UTF8.GetString(PqcEnvelope.Open(keys, env, aad)));

        var bytes = Convert.FromBase64String(env.Ciphertext);
        bytes[0] ^= 1;
        Assert.Throws<CryptographicException>(() => PqcEnvelope.Open(keys, env with { Ciphertext = Convert.ToBase64String(bytes) }, aad));
        using var other = HybridKeyPair.Generate();
        Assert.Throws<CryptographicException>(() => PqcEnvelope.Open(other, env, aad));
    }

    [TestMethod]
    public void Signature_service_signs_and_verifier_enforces_key_id()
    {
        using var signer = new PqcSignatureService(Options.Create(new CryptoOptions { KeyId = "k1" }), NullLogger<PqcSignatureService>.Instance);
        using var verifier = new HybridSignatureVerifier(signer.PublicKeys);
        var digest = SHA256.HashData("payload"u8);

        var sig = signer.Sign(digest);

        Assert.IsTrue(verifier.Verify(digest, sig));
        Assert.IsFalse(verifier.Verify(SHA256.HashData("other"u8), sig));
        Assert.IsFalse(verifier.Verify(digest, sig with { ClassicSignature = Convert.ToBase64String(new byte[96]) }));
        if (verifier.ServerHasPqc)
        {
            Assert.IsFalse(verifier.Verify(digest, sig with { PqcSignature = null }), "Stripping the PQC part (downgrade) must fail.");
        }
    }

    [TestMethod]
    public void Payload_protection_is_bound_to_the_job_id()
    {
        var protector = new PayloadProtectionService(Options.Create(new JobDistOptions { PayloadProtectionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }),
            NullLogger<PayloadProtectionService>.Instance);
        var id = Guid.NewGuid();

        var blob = protector.Protect(id, "card=4111");

        Assert.DoesNotContain("4111", blob);
        Assert.AreEqual("card=4111", protector.Unprotect(id, blob));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), blob));
    }

    [TestMethod]
    [DataRow("image.resize", true)]
    [DataRow("a-b_c.1", true)]
    [DataRow(".hidden", false)]
    [DataRow("drop table;", false)]
    [DataRow("", false)]
    public void Job_type_validation(string type, bool valid) => Assert.AreEqual(valid, InputGuard.IsValidJobType(type));

    [TestMethod]
    public void CleanText_strips_control_characters_and_truncates()
    {
        Assert.AreEqual("abc", InputGuard.CleanText("a\u0000b\u001bc", 10));
        Assert.AreEqual(5, InputGuard.CleanText(new string('x', 50), 5)!.Length);
    }

    // ------------------------------------------------------------------------------------------------ AI tools

    [TestMethod]
    public void Ai_functions_have_snake_case_names_and_side_effect_tools_are_optional()
    {
        var all = JobDistAiFunctions.Create(includeSideEffects: true).Select(f => f.Name).ToList();
        var readOnly = JobDistAiFunctions.Create(includeSideEffects: false).Select(f => f.Name).ToList();

        CollectionAssert.IsSubsetOf(new[] { "assess_job_risk", "assess_submission_risk", "assess_worker_risk", "detect_anomalies", "recommend_workers" }, readOnly);
        Assert.Contains("submit_job", all);
        Assert.DoesNotContain("submit_job", readOnly);
        Assert.DoesNotContain("cancel_job", readOnly);
        Assert.IsTrue(all.All(n => n == n.ToLowerInvariant()));
    }

    [TestMethod]
    public async Task Ai_function_invocation_runs_the_same_business_logic()
    {
        using var host = new TestHost();
        await host.RegisterAsync("w1");
        var tool = JobDistAiFunctions.Create(false).Single(f => f.Name == "assess_submission_risk");

        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["jobType"] = "demo" }) { Services = host.Services });

        var json = JsonSerializer.SerializeToElement(result);
        Assert.AreEqual("submission:demo", json.GetProperty("subject").GetString());
        Assert.IsTrue(json.TryGetProperty("score", out _));
    }

    [TestMethod]
    public async Task Side_effect_tool_is_refused_when_disabled()
    {
        using var host = new TestHost(new() { ["Mcp:AllowSideEffects"] = "false" });
        var submit = JobDistAiFunctions.Create(true).Single(f => f.Name == "submit_job");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await submit.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["jobType"] = "demo" }) { Services = host.Services }));
        StringAssert.Contains(ex.Message, "AllowSideEffects");
    }

    [TestMethod]
    public async Task Side_effect_tool_submits_when_enabled()
    {
        using var host = new TestHost(new() { ["Mcp:AllowSideEffects"] = "true" });
        var submit = JobDistAiFunctions.Create(true).Single(f => f.Name == "submit_job");

        var result = await submit.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["jobType"] = "demo", ["priority"] = 70 }) { Services = host.Services });

        var id = JsonSerializer.SerializeToElement(result).GetProperty("jobId").GetGuid();
        var job = await host.Jobs.GetAsync(id, CancellationToken.None);
        Assert.AreEqual(70, job.Priority);
        Assert.AreEqual(JobStatus.Pending, job.Status);
    }
}
