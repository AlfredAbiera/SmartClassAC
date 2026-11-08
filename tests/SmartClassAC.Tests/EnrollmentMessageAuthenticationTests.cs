using System.Text;
using SmartClassAC.Services;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class EnrollmentMessageAuthenticationTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] Challenge = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"slot\":2}");

    [Fact]
    public void ComputeTag_MatchesIndependentTestVector()
    {
        var tag = EnrollmentMessageAuthentication.ComputeTag(Key, false, "1000000001", "claim", Challenge, Body);
        Assert.Equal("D91FBB7E3DD3B87B55F0AB95C17B4CD2E1BB8EDC36987B1D5EE17C4DF9497ACD", Convert.ToHexString(tag));
        Assert.True(EnrollmentMessageAuthentication.VerifyTag(Key, false, "1000000001", "claim", Challenge, Body, tag));
    }

    [Fact]
    public void VerifyTag_RejectsChangesAndWrongKey()
    {
        var tag = EnrollmentMessageAuthentication.ComputeTag(Key, false, "1000000001", "claim", Challenge, Body);
        var otherKey = Key.ToArray();
        otherKey[0] ^= 1;
        var otherChallenge = Challenge.ToArray();
        otherChallenge[0] ^= 1;
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(otherKey, false, "1000000001", "claim", Challenge, Body, tag));
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(Key, true, "1000000001", "claim", Challenge, Body, tag));
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(Key, false, "1000000002", "claim", Challenge, Body, tag));
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(Key, false, "1000000001", "finish", Challenge, Body, tag));
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(Key, false, "1000000001", "claim", otherChallenge, Body, tag));
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(Key, false, "1000000001", "claim", Challenge, Encoding.UTF8.GetBytes("{\"slot\":3}"), tag));
        tag[0] ^= 1;
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(Key, false, "1000000001", "claim", Challenge, Body, tag));
        Assert.False(EnrollmentMessageAuthentication.VerifyTag(Key, false, "1000000001", "claim", Challenge, Body, tag.AsSpan(1)));
    }

    [Fact]
    public void ComputeTag_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => EnrollmentMessageAuthentication.ComputeTag(new byte[31], false, "1", "claim", Challenge, Body));
        Assert.Throws<ArgumentException>(() => EnrollmentMessageAuthentication.ComputeTag(Key, false, "1", "claim", new byte[31], Body));
        Assert.Throws<ArgumentException>(() => EnrollmentMessageAuthentication.ComputeTag(Key, false, "1\n2", "claim", Challenge, Body));
        Assert.Throws<ArgumentException>(() => EnrollmentMessageAuthentication.ComputeTag(Key, false, "1", "Claim", Challenge, Body));
        Assert.Throws<ArgumentException>(() => EnrollmentMessageAuthentication.ComputeTag(Key, false, "1", "claim\nresponse", Challenge, Body));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnrollmentMessageAuthentication.ComputeTag(Key, false, "1", "claim", Challenge, new byte[4097]));
    }

    [Fact]
    public void ComputeTag_MatchesRecoveryResponseVector()
    {
        var body = Encoding.ASCII.GetBytes(
            "11111111-2222-3333-4444-555555555555\n66666666-7777-8888-9999-aaaaaaaaaaaa\n2\n" +
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\ncompleted\n");
        var tag = EnrollmentMessageAuthentication.ComputeTag(Key, true, "1000000001", "recover", Challenge, body);
        Assert.Equal("368671F53599CC5B5CC78BD08B35FD248F3DAB42A2E291857E1257A967279D2A", Convert.ToHexString(tag));
        Assert.True(EnrollmentMessageAuthentication.VerifyTag(Key, true, "1000000001", "recover", Challenge, body, tag));
    }

    [Fact]
    public void ComputeTag_MatchesEmptyAndMaximumLengthVectors()
    {
        Assert.Equal("4D16FBFBFB1D293A1A32F9DECE6085A06EFB6854198D8C2ED79F85327910609D",
            Convert.ToHexString(EnrollmentMessageAuthentication.ComputeTag(Key, true, "1000000001", "claim", Challenge, Array.Empty<byte>())));
        Assert.Equal("98D404134F275C302812D6422269F651247A943BAF60B9183D6DBF9746564E79",
            Convert.ToHexString(EnrollmentMessageAuthentication.ComputeTag(Key, false, new string('1', 23), new string('a', 32),
                Challenge, Enumerable.Repeat((byte)'x', 4096).ToArray())));
    }
}
