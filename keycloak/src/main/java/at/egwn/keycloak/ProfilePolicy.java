package at.egwn.keycloak;

import org.keycloak.models.KeycloakSession;
import org.keycloak.representations.userprofile.config.UPAttribute;
import org.keycloak.representations.userprofile.config.UPConfig;
import org.keycloak.userprofile.UserProfileProvider;

final class ProfilePolicy {
    private ProfilePolicy() {}

    static void requireSafeConfiguration(KeycloakSession session) {
        UPConfig config = session.getProvider(UserProfileProvider.class).getConfiguration();
        if (config == null || config.getAttributes() == null
                || config.getUnmanagedAttributePolicy() != null
                || config.getAttribute(RegistrationAction.CODE) != null) {
            throw new BackendClient.BackendFailure();
        }
        UPAttribute org = config.getAttribute(FactorySupport.ORGANIZATION);
        if (org == null || org.getPermissions() == null || org.getPermissions().getEdit() == null
                || org.getPermissions().getEdit().contains("user")) {
            throw new BackendClient.BackendFailure();
        }
    }
}
