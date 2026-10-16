# syntax=docker/dockerfile:1.7
FROM quay.io/keycloak/keycloak:26.7.3 AS build
ENV KC_DB=postgres
ENV KC_HEALTH_ENABLED=true
ENV KC_METRICS_ENABLED=true
RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.7.3
COPY --from=build --chown=1000:0 /opt/keycloak/ /opt/keycloak/
COPY --chown=1000:0 infra/keycloak/realm-production.json /opt/keycloak/data/import/excellore-realm.json
COPY --chown=1000:0 infra/keycloak/themes/excellore /opt/keycloak/themes/excellore
COPY --chown=1000:0 infra/keycloak/password-blacklists/excellore-common.txt /opt/keycloak/data/password-blacklists/excellore-common.txt
USER 1000:0
