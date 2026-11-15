package at.egwn.keycloak;

import java.util.function.Supplier;
import jakarta.ws.rs.core.Response;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.AuthenticationFlowError;
import org.keycloak.authentication.Authenticator;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;

final class MembershipAuthenticator implements Authenticator {
    private final Supplier<Backend> backend;
    private final String grafanaClientId;

    MembershipAuthenticator(Supplier<Backend> backend, String grafanaClientId) {
        this.backend = backend;
        this.grafanaClientId = grafanaClientId;
    }

    @Override
    public void authenticate(AuthenticationFlowContext context) {
        if (!grafanaClientId.equals(context.getAuthenticationSession().getClient().getClientId())) {
            context.success();
            return;
        }
        UserModel user = context.getUser();
        try {
            ProfilePolicy.requireSafeConfiguration(context.getSession());
            if (user == null) {
                throw new BackendClient.BackendFailure();
            }
            Backend.Membership membership = backend.get().membership(user.getId());
            user.setSingleAttribute(FactorySupport.ORGANIZATION, membership.organization());
            context.success();
        } catch (BackendClient.BackendFailure ex) {
            if (user != null) {
                user.removeAttribute(FactorySupport.ORGANIZATION);
            }
            context.failure(AuthenticationFlowError.ACCESS_DENIED,
                    context.form().setError("egwnMembershipFailed").createErrorPage(Response.Status.FORBIDDEN));
        }
    }

    @Override public void action(AuthenticationFlowContext context) { authenticate(context); }
    @Override public boolean requiresUser() { return false; }
    @Override public boolean configuredFor(KeycloakSession session, RealmModel realm, UserModel user) { return true; }
    @Override public void setRequiredActions(KeycloakSession session, RealmModel realm, UserModel user) {}
    @Override public void close() {}
}
