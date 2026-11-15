package at.egwn.keycloak;

import static org.junit.jupiter.api.Assertions.*;
import static org.mockito.ArgumentMatchers.*;
import static org.mockito.Mockito.*;
import jakarta.ws.rs.core.MultivaluedHashMap;
import jakarta.ws.rs.core.Response;
import java.util.List;
import java.util.ArrayList;
import java.util.Set;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.AuthenticationFlowError;
import org.keycloak.authentication.AuthenticationFlowException;
import org.keycloak.authentication.FormContext;
import org.keycloak.authentication.ValidationContext;
import org.keycloak.forms.login.LoginFormsProvider;
import org.keycloak.http.HttpRequest;
import org.keycloak.models.ClientModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.KeycloakTransactionManager;
import org.keycloak.models.UserModel;
import org.keycloak.representations.userprofile.config.UPAttribute;
import org.keycloak.representations.userprofile.config.UPAttributePermissions;
import org.keycloak.representations.userprofile.config.UPConfig;
import org.keycloak.sessions.AuthenticationSessionModel;
import org.keycloak.userprofile.UserProfileProvider;

class FlowTest {
    private final Backend backend = mock(Backend.class);
    private final KeycloakSession session = mock(KeycloakSession.class);
    private final AuthenticationSessionModel auth = mock(AuthenticationSessionModel.class);
    private final UserModel user = mock(UserModel.class);
    private final KeycloakTransactionManager transaction = mock(KeycloakTransactionManager.class);
    private UPConfig config;

    @BeforeEach
    void configure() {
        when(session.getTransactionManager()).thenReturn(transaction);
        UserProfileProvider profile = mock(UserProfileProvider.class);
        when(session.getProvider(UserProfileProvider.class)).thenReturn(profile);
        config = new UPConfig();
        UPAttribute attribute = new UPAttribute();
        attribute.setName("egwn_org");
        attribute.setPermissions(new UPAttributePermissions(Set.of("admin"), Set.of("admin")));
        config.setAttributes(new ArrayList<>(List.of(attribute)));
        when(profile.getConfiguration()).thenReturn(config);
        when(user.getId()).thenReturn("immutable-user-id");
    }

    private ValidationContext validation(MultivaluedHashMap<String, String> data) {
        ValidationContext context = mock(ValidationContext.class);
        HttpRequest request = mock(HttpRequest.class);
        when(context.getHttpRequest()).thenReturn(request);
        when(request.getDecodedFormParameters()).thenReturn(data);
        when(context.getAuthenticationSession()).thenReturn(auth);
        when(context.getSession()).thenReturn(session);
        return context;
    }

    @Test
    void validateResolvesAndStoresOnlyTicket() {
        var data = new MultivaluedHashMap<String, String>();
        data.putSingle(RegistrationAction.CODE, "raw-secret");
        data.putSingle("egwn_org", "attacker-org");
        when(backend.resolve("raw-secret")).thenReturn(new Backend.Resolved("opaque-ticket", "org"));
        ValidationContext context = validation(data);
        new RegistrationAction(() -> backend).validate(context);
        verify(backend).resolve("raw-secret");
        verify(auth).setAuthNote(RegistrationAction.TICKET, "opaque-ticket");
        verify(auth, never()).setAuthNote(anyString(), eq("raw-secret"));
        assertFalse(data.containsKey(RegistrationAction.CODE));
        assertFalse(data.containsKey("egwn_org"));
        verify(context).success();
    }

    @Test
    void invalidDuplicateAndOutageNeverSucceed() {
        when(backend.resolve("outage")).thenThrow(new BackendClient.BackendFailure());
        for (List<String> values : List.of(List.<String>of(), List.of(""), List.of("a", "b"), List.of("outage"))) {
            var data = new MultivaluedHashMap<String, String>();
            data.put(RegistrationAction.CODE, values);
            ValidationContext context = validation(data);
            new RegistrationAction(() -> backend).validate(context);
            verify(context, never()).success();
            verify(context).validationError(eq(data), anyList());
            verify(context).error(anyString());
            assertFalse(data.containsKey(RegistrationAction.CODE));
        }
    }

    private FormContext successContext() {
        FormContext context = mock(FormContext.class);
        when(context.getAuthenticationSession()).thenReturn(auth);
        when(context.getSession()).thenReturn(session);
        when(context.getUser()).thenReturn(user);
        when(auth.getAuthNote(RegistrationAction.TICKET)).thenReturn("ticket");
        return context;
    }

    @Test
    void assignUsesCreatedIdAndReplacesOrganization() {
        when(backend.assign("ticket", "immutable-user-id")).thenReturn(new Backend.Membership("guid", "assigned-org"));
        new RegistrationAction(() -> backend).success(successContext());
        verify(backend).assign("ticket", "immutable-user-id");
        verify(user).setSingleAttribute("egwn_org", "assigned-org");
        verify(auth).removeAuthNote(RegistrationAction.TICKET);
        verify(transaction, never()).setRollbackOnly();
    }

    @Test
    void assignOutageRollsBackUserCreation() {
        when(backend.assign(anyString(), anyString())).thenThrow(new BackendClient.BackendFailure());
        assertThrows(AuthenticationFlowException.class,
                () -> new RegistrationAction(() -> backend).success(successContext()));
        verify(transaction).setRollbackOnly();
        verify(user, never()).setSingleAttribute(anyString(), anyString());
        verify(auth).removeAuthNote(RegistrationAction.TICKET);
    }

    @Test
    void missingTicketOrUserRollsBack() {
        FormContext context = successContext();
        when(auth.getAuthNote(RegistrationAction.TICKET)).thenReturn(null);
        assertThrows(AuthenticationFlowException.class, () -> new RegistrationAction(() -> backend).success(context));
        verify(transaction).setRollbackOnly();
        verifyNoInteractions(backend);
    }

    private AuthenticationFlowContext browser(String clientId) {
        AuthenticationFlowContext context = mock(AuthenticationFlowContext.class);
        ClientModel client = mock(ClientModel.class);
        when(client.getClientId()).thenReturn(clientId);
        when(auth.getClient()).thenReturn(client);
        when(context.getAuthenticationSession()).thenReturn(auth);
        when(context.getSession()).thenReturn(session);
        when(context.getUser()).thenReturn(user);
        return context;
    }

    @Test
    void everyGrafanaLoginFetchesMembershipAndReplacesSingleAttribute() {
        AuthenticationFlowContext context = browser("grafana");
        when(backend.membership("immutable-user-id")).thenReturn(new Backend.Membership("guid", "fresh-org"));
        var authenticator = new MembershipAuthenticator(() -> backend, "grafana");
        authenticator.authenticate(context);
        authenticator.authenticate(context);
        verify(backend, times(2)).membership("immutable-user-id");
        verify(user, times(2)).setSingleAttribute("egwn_org", "fresh-org");
        verify(context, times(2)).success();
    }

    @Test
    void inactiveOrUnavailableMembershipDeniesAndRemovesStaleClaim() {
        AuthenticationFlowContext context = browser("grafana");
        LoginFormsProvider form = mock(LoginFormsProvider.class);
        when(context.form()).thenReturn(form);
        when(form.setError("egwnMembershipFailed")).thenReturn(form);
        Response response = mock(Response.class);
        when(form.createErrorPage(Response.Status.FORBIDDEN)).thenReturn(response);
        when(backend.membership(anyString())).thenThrow(new BackendClient.BackendFailure());
        new MembershipAuthenticator(() -> backend, "grafana").authenticate(context);
        verify(context).failure(AuthenticationFlowError.ACCESS_DENIED, response);
        verify(context, never()).success();
        verify(user).removeAttribute("egwn_org");
    }

    @Test
    void otherClientsBypassExplicitlyWithoutBackendConfiguration() {
        AuthenticationFlowContext context = browser("account-console");
        new MembershipAuthenticator(() -> { throw new AssertionError(); }, "grafana").authenticate(context);
        verify(context).success();
        verifyNoInteractions(backend, user);
    }

    @Test
    void profileMisconfigurationFailsClosed() {
        config.setUnmanagedAttributePolicy(UPConfig.UnmanagedAttributePolicy.ENABLED);
        assertThrows(BackendClient.BackendFailure.class, () -> ProfilePolicy.requireSafeConfiguration(session));
        config.setUnmanagedAttributePolicy(null);
        config.getAttribute("egwn_org").getPermissions().setEdit(Set.of("user", "admin"));
        assertThrows(BackendClient.BackendFailure.class, () -> ProfilePolicy.requireSafeConfiguration(session));
        config.getAttribute("egwn_org").getPermissions().setEdit(Set.of("admin"));
        UPAttribute code = new UPAttribute();
        code.setName(RegistrationAction.CODE);
        config.addOrReplaceAttribute(code);
        assertThrows(BackendClient.BackendFailure.class, () -> ProfilePolicy.requireSafeConfiguration(session));
    }
}
