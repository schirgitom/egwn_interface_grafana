package at.egwn.keycloak;

import static org.junit.jupiter.api.Assertions.*;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

class BackendClientTest {
    private HttpServer server;
    private String url;
    private final AtomicReference<String> body = new AtomicReference<>();
    private final AtomicReference<String> authorization = new AtomicReference<>();
    private final AtomicReference<String> path = new AtomicReference<>();
    private volatile String response;
    private volatile int status;
    private volatile long delay;
    private volatile long bodyDelay;

    @BeforeEach
    void start() throws IOException {
        response = "{\"ticket\":\"signed-ticket\",\"organization\":\"customer-a\"}";
        status = 200;
        delay = 0;
        bodyDelay = 0;
        server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        server.createContext("/", exchange -> {
            path.set(exchange.getRequestURI().getPath());
            authorization.set(exchange.getRequestHeaders().getFirst("Authorization"));
            body.set(new String(exchange.getRequestBody().readAllBytes(), StandardCharsets.UTF_8));
            try {
                if (delay != 0) Thread.sleep(delay);
                byte[] bytes = response.getBytes(StandardCharsets.UTF_8);
                exchange.getResponseHeaders().set("Content-Type", "application/json");
                exchange.getResponseHeaders().set("Location", url + "/redirect");
                exchange.sendResponseHeaders(status, bytes.length);
                if (bodyDelay != 0) Thread.sleep(bodyDelay);
                exchange.getResponseBody().write(bytes);
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
            } finally {
                exchange.close();
            }
        });
        server.start();
        url = "http://127.0.0.1:" + server.getAddress().getPort();
    }

    @AfterEach void stop() { server.stop(0); }
    private BackendClient client() { return new BackendClient(url, "internal-secret", true, Duration.ofSeconds(2)); }

    @Test
    void resolveSendsJsonAndBearerWithoutAlteringCode() throws Exception {
        assertEquals(new Backend.Resolved("signed-ticket", "customer-a"), client().resolve("code\"\\ä"));
        assertEquals("/internal/registration/resolve", path.get());
        assertEquals("Bearer internal-secret", authorization.get());
        assertEquals("code\"\\ä", new ObjectMapper().readTree(body.get()).get("code").asText());
    }

    @Test
    void assignAndMembershipUseImmutableIdAndValidateGuid() throws Exception {
        response = "{\"customerId\":\"8a8b8b8a-72b6-4f4a-9c16-0ce57d4567a0\",\"organization\":\"customer-b\"}";
        assertEquals("customer-b", client().assign("ticket", "immutable-id").organization());
        assertEquals("/internal/registration/assign", path.get());
        JsonNode payload = new ObjectMapper().readTree(body.get());
        assertEquals("immutable-id", payload.get("userId").asText());
        assertEquals("ticket", payload.get("ticket").asText());
        assertFalse(payload.has("code"));
        assertEquals("customer-b", client().membership("immutable-id").organization());
        assertEquals("/internal/registration/membership", path.get());
        assertEquals(1, new ObjectMapper().readTree(body.get()).size());
    }

    @Test
    void refusesUnsafeConfiguration() {
        for (String invalid : new String[] {url, "ftp://backend", "https://user:pass@backend",
                "https://backend?token=secret", "https://backend/#fragment", "", "not a uri"}) {
            assertThrows(BackendClient.BackendFailure.class,
                    () -> new BackendClient(invalid, "secret", false, Duration.ofSeconds(1)));
        }
        assertThrows(BackendClient.BackendFailure.class,
                () -> new BackendClient("https://backend", "", false, Duration.ofSeconds(1)));
        assertDoesNotThrow(() -> new BackendClient("https://backend", "secret", false, Duration.ofSeconds(1)));
    }

    @Test
    void rejectsErrorsRedirectsAndInvalidBodiesWithoutLeakingDetails() {
        for (int code : new int[] {400, 401, 403, 302, 500, 503}) {
            status = code;
            response = "secret-response-code";
            var failure = assertThrows(BackendClient.BackendFailure.class, () -> client().resolve("secret-code"));
            assertNull(failure.getCause());
            assertFalse(failure.toString().contains("secret"));
        }
        status = 200;
        for (String invalid : new String[] {"not-json-secret", "null", "[]", "{}", "{\"ticket\":1}",
                "{\"ticket\":\"valid\",\"organization\":\"\"}", "x".repeat(17000)}) {
            response = invalid;
            assertThrows(BackendClient.BackendFailure.class, () -> client().resolve("code"));
        }
    }

    @Test
    void rejectsMissingOrInvalidMembership() {
        for (String invalid : new String[] {"{}", "{\"customerId\":\"not-a-guid\",\"organization\":\"org\"}",
                "{\"customerId\":\"1-1-1-1-1\",\"organization\":\"org\"}"}) {
            response = invalid;
            assertThrows(BackendClient.BackendFailure.class, () -> client().membership("id"));
        }
        assertThrows(BackendClient.BackendFailure.class, () -> client().resolve(" "));
        assertThrows(BackendClient.BackendFailure.class, () -> client().resolve("x".repeat(513)));
    }

    @Test
    void timeoutFailsClosed() {
        delay = 400;
        BackendClient client = new BackendClient(url, "secret", true, Duration.ofMillis(100));
        assertThrows(BackendClient.BackendFailure.class, () -> client.resolve("code"));
    }

    @Test
    void slowBodyAlsoHasBoundedTimeout() {
        bodyDelay = 400;
        BackendClient client = new BackendClient(url, "secret", true, Duration.ofMillis(100));
        assertThrows(BackendClient.BackendFailure.class, () -> client.resolve("code"));
    }

    @Test
    void connectionFailureFailsClosed() {
        server.stop(0);
        assertThrows(BackendClient.BackendFailure.class, () -> client().membership("id"));
    }
}
