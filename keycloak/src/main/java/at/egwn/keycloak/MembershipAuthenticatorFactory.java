package at.egwn.keycloak;

import org.keycloak.authentication.Authenticator;
import org.keycloak.authentication.AuthenticatorFactory;
import org.keycloak.models.KeycloakSession;

public final class MembershipAuthenticatorFactory extends FactorySupport implements AuthenticatorFactory {
    public static final String ID = "egwn-membership";
    @Override public String getId() { return ID; }
    @Override public String getDisplayType() { return "EGWN active Grafana membership"; }
    @Override public String getHelpText() {
        return "Required after authentication, including cookie SSO. Checks live Grafana membership.";
    }
    @Override public Authenticator create(KeycloakSession session) {
        String configured = System.getenv("EGWN_GRAFANA_CLIENT_ID");
        String clientId = configured == null || configured.isBlank() ? "grafana" : configured;
        return new MembershipAuthenticator(BackendClient::fromEnvironment, clientId);
    }
}
