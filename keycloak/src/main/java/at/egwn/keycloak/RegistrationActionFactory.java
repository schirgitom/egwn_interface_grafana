package at.egwn.keycloak;

import org.keycloak.authentication.FormAction;
import org.keycloak.authentication.FormActionFactory;
import org.keycloak.models.KeycloakSession;

public final class RegistrationActionFactory extends FactorySupport implements FormActionFactory {
    public static final String ID = "egwn-registration";
    @Override public String getId() { return ID; }
    @Override public String getDisplayType() { return "EGWN registration assignment"; }
    @Override public String getHelpText() {
        return "Required after Registration User Profile Creation. Resolves a code, then assigns the created user.";
    }
    @Override public FormAction create(KeycloakSession session) {
        return new RegistrationAction(BackendClient::fromEnvironment);
    }
}
