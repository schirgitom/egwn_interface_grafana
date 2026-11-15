package at.egwn.keycloak;

interface Backend {
    Resolved resolve(String code);
    Membership assign(String ticket, String userId);
    Membership membership(String userId);

    record Resolved(String ticket, String organization) {}
    record Membership(String customerId, String organization) {}
}
