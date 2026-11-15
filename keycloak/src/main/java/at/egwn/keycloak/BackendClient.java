package at.egwn.keycloak;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.ByteBuffer;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Flow;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

final class BackendClient implements Backend {
    private static final ObjectMapper JSON = new ObjectMapper();
    private static BackendClient environmentClient;
    private final URI base;
    private final String token;
    private final HttpClient client;
    private final Duration timeout;

    static synchronized BackendClient fromEnvironment() {
        if (environmentClient == null) {
            environmentClient = new BackendClient(System.getenv("EGWN_BACKEND_URL"), System.getenv("EGWN_INTERNAL_TOKEN"),
                    "true".equals(System.getenv("EGWN_ALLOW_INSECURE_HTTP")), Duration.ofSeconds(5));
        }
        return environmentClient;
    }

    BackendClient(String url, String token, boolean allowHttp, Duration timeout) {
        try {
            base = URI.create(url == null ? "" : url);
            if (base.getHost() == null || base.getUserInfo() != null || base.getQuery() != null
                    || base.getFragment() != null || !("https".equals(base.getScheme())
                    || (allowHttp && "http".equals(base.getScheme())))) {
                throw new IllegalArgumentException();
            }
            if (token == null || token.isBlank() || token.contains("\r") || token.contains("\n")) {
                throw new IllegalArgumentException();
            }
        } catch (IllegalArgumentException ex) {
            throw new BackendFailure();
        }
        this.token = token;
        this.timeout = timeout;
        client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(2))
                .followRedirects(HttpClient.Redirect.NEVER).build();
    }

    @Override
    public Resolved resolve(String code) {
        if (code == null || code.isBlank() || code.length() > 512) {
            throw new BackendFailure();
        }
        JsonNode result = post("resolve", Map.of("code", code));
        return new Resolved(text(result, "ticket", 8192), text(result, "organization", 255));
    }

    @Override
    public Membership assign(String ticket, String userId) {
        if (ticket == null || ticket.isBlank() || ticket.length() > 8192 || userId == null || userId.isBlank()) {
            throw new BackendFailure();
        }
        return membershipResult(post("assign", Map.of("ticket", ticket, "userId", userId)));
    }

    @Override
    public Membership membership(String userId) {
        if (userId == null || userId.isBlank()) {
            throw new BackendFailure();
        }
        return membershipResult(post("membership", Map.of("userId", userId)));
    }

    private Membership membershipResult(JsonNode result) {
        String customerId = text(result, "customerId", 36);
        try {
            if (!UUID.fromString(customerId).toString().equalsIgnoreCase(customerId)) {
                throw new IllegalArgumentException();
            }
        } catch (IllegalArgumentException ex) {
            throw new BackendFailure();
        }
        return new Membership(customerId, text(result, "organization", 255));
    }

    private JsonNode post(String operation, Map<String, String> payload) {
        CompletableFuture<HttpResponse<byte[]>> pending = null;
        try {
            String prefix = base.toString().replaceAll("/+$", "");
            HttpRequest request = HttpRequest.newBuilder(URI.create(prefix + "/internal/registration/" + operation))
                    .timeout(timeout).header("Authorization", "Bearer " + token)
                    .header("Content-Type", "application/json").header("Accept", "application/json")
                    .POST(HttpRequest.BodyPublishers.ofString(JSON.writeValueAsString(payload))).build();
            pending = client.sendAsync(request, info -> new LimitedBodySubscriber());
            HttpResponse<byte[]> response = pending.get(timeout.toMillis(), TimeUnit.MILLISECONDS);
            if (response.statusCode() != 200) {
                throw new BackendFailure();
            }
            JsonNode result = JSON.readTree(response.body());
            if (result == null || !result.isObject()) {
                throw new BackendFailure();
            }
            return result;
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            throw new BackendFailure();
        } catch (IOException | IllegalArgumentException | ExecutionException | TimeoutException ex) {
            // Deliberately discard causes: HTTP/JSON exceptions can contain secrets or response bodies.
            throw new BackendFailure();
        } finally {
            if (pending != null && !pending.isDone()) {
                pending.cancel(true);
            }
        }
    }

    private static final class LimitedBodySubscriber implements HttpResponse.BodySubscriber<byte[]> {
        private final HttpResponse.BodySubscriber<byte[]> delegate = HttpResponse.BodySubscribers.ofByteArray();
        private Flow.Subscription subscription;
        private int received;

        @Override public CompletionStage<byte[]> getBody() { return delegate.getBody(); }
        @Override public void onSubscribe(Flow.Subscription value) {
            subscription = value;
            delegate.onSubscribe(value);
        }
        @Override public void onNext(List<ByteBuffer> buffers) {
            for (ByteBuffer buffer : buffers) {
                received += buffer.remaining();
                if (received > 16384) {
                    subscription.cancel();
                    delegate.onError(new IOException("Response exceeds size limit"));
                    return;
                }
            }
            delegate.onNext(buffers);
        }
        @Override public void onError(Throwable error) { delegate.onError(error); }
        @Override public void onComplete() { delegate.onComplete(); }
    }

    private static String text(JsonNode node, String field, int maximum) {
        JsonNode value = node.get(field);
        if (value == null || !value.isTextual() || value.textValue().isBlank()
                || value.textValue().length() > maximum) {
            throw new BackendFailure();
        }
        return value.textValue();
    }

    static final class BackendFailure extends RuntimeException {
        BackendFailure() {
            super("EGWN membership verification failed");
        }
    }
}
