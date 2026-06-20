using System.Threading.Tasks;
using PurrNet.Authentication;
using PurrNet.Transports;
using UnityEngine;

public class VersionAuthenticator : AuthenticationBehaviour<string>
{
    protected override Task<AuthenticationRequest<string>> GetClientPayload()
    {
        return Task.FromResult(new AuthenticationRequest<string>(Application.version));
    }

    protected override Task<AuthenticationResponse> ValidateClientPayload(Connection conn, string payload)
    {
        return Task.FromResult<AuthenticationResponse>(payload == Application.version);
    }

    protected override void UnAuthenticateClient(Connection conn) { }
}
