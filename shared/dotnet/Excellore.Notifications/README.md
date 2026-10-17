# Excellore.Notifications

Provider-neutral notification delivery primitives shared by trusted Excellore services.

The package currently supplies an authenticated SMTP email transport with explicit TLS modes,
bounded timeouts, cancellation, and header/address validation. It never logs message content or
recipient addresses. Business notification policy and templates remain outside the transport.
