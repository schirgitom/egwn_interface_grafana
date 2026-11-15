FROM maven:3.9.11-eclipse-temurin-21 AS builder
WORKDIR /workspace
COPY pom.xml .
COPY src ./src
RUN mvn --batch-mode --no-transfer-progress verify

FROM quay.io/keycloak/keycloak:26.8.2 AS keycloak-build
COPY --from=builder --chown=keycloak:keycloak /workspace/target/egwn-keycloak.jar /opt/keycloak/providers/egwn-keycloak.jar
RUN /opt/keycloak/bin/kc.sh build --db=postgres --health-enabled=true

FROM quay.io/keycloak/keycloak:26.8.2
COPY --from=keycloak-build /opt/keycloak/ /opt/keycloak/
COPY --chown=keycloak:keycloak theme/egwn/ /opt/keycloak/themes/egwn/
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--optimized"]
