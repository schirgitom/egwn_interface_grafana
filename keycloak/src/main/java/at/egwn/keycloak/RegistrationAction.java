package at.egwn.keycloak;

import java.util.List;
import java.util.function.Supplier;
import jakarta.ws.rs.core.MultivaluedMap;
import org.keycloak.authentication.AuthenticationFlowError;
import org.keycloak.authentication.AuthenticationFlowException;
import org.keycloak.authentication.FormAction;
import org.keycloak.authentication.FormContext;
import org.keycloak.authentication.ValidationContext;
import org.keycloak.events.Errors;
import org.keycloak.forms.login.LoginFormsProvider;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.models.utils.FormMessage;

final class RegistrationAction implements FormAction {
    static final String CODE = "egwn_registration_code";
    static final String TICKET = "egwn.registration.ticket";
    private final Supplier<Backend> backend;

    RegistrationAction(Supplier<Backend> backend) {
        this.backend = backend;
    }

    @Override
    public void buildPage(FormContext context, LoginFormsProvider form) {
        context.getAuthenticationSession().removeAuthNote(TICKET);
    }

    @Override
    public void validate(ValidationContext context) {
        context.getAuthenticationSession().removeAuthNote(TICKET);
        MultivaluedMap<String, String> data = context.getHttpRequest().getDecodedFormParameters();
        List<String> codes = data.remove(CODE);
        // Never allow a posted organization to participate in profile creation or error redisplay.
        data.remove(FactorySupport.ORGANIZATION);
        try {
            ProfilePolicy.requireSafeConfiguration(context.getSession());
            if (codes == null || codes.size() != 1 || codes.getFirst() == null
                    || codes.getFirst().isBlank() || codes.getFirst().length() > 512) {
                throw new BackendClient.BackendFailure();
            }
            Backend.Resolved resolved = backend.get().resolve(codes.getFirst());
            context.getAuthenticationSession().setAuthNote(TICKET, resolved.ticket());
            context.success();
        } catch (BackendClient.BackendFailure ex) {
            context.error(Errors.INVALID_REGISTRATION);
            context.validationError(data, List.of(new FormMessage(CODE, "egwnRegistrationFailed")));
        }
    }

    @Override
    public void success(FormContext context) {
        String ticket = context.getAuthenticationSession().getAuthNote(TICKET);
        context.getAuthenticationSession().removeAuthNote(TICKET);
        try {
            ProfilePolicy.requireSafeConfiguration(context.getSession());
            UserModel user = context.getUser();
            if (ticket == null || user == null) {
                throw new BackendClient.BackendFailure();
            }
            Backend.Membership membership = backend.get().assign(ticket, user.getId());
            user.setSingleAttribute(FactorySupport.ORGANIZATION, membership.organization());
        } catch (RuntimeException ex) {
            // success() cannot issue validation errors; explicitly roll back creation and abort the flow.
            context.getSession().getTransactionManager().setRollbackOnly();
            throw new AuthenticationFlowException(AuthenticationFlowError.INTERNAL_ERROR,
                    Errors.INVALID_REGISTRATION, "egwnRegistrationFailed");
        }
    }

    @Override public boolean requiresUser() { return false; }
    @Override public boolean configuredFor(KeycloakSession session, RealmModel realm, UserModel user) { return true; }
    @Override public void setRequiredActions(KeycloakSession session, RealmModel realm, UserModel user) {}
    @Override public void close() {}
}
