# Redis 8.12 is at the release-candidate stage and has no official Docker image yet, so the
# 8.12 server is built from the source tag. Swap this whole file for `image: redis:8.12` in
# docker-compose.yml once that exists. jemalloc (the default allocator) is kept deliberately:
# the demo compares MEMORY USAGE numbers, and the allocator is part of those numbers.
FROM debian:bookworm-slim AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
ARG REDIS_TAG=8.12-rc2
RUN curl -fsSL "https://github.com/redis/redis/archive/refs/tags/${REDIS_TAG}.tar.gz" | tar xz -C /usr/src \
    && make -C "/usr/src/redis-${REDIS_TAG}" -j"$(nproc)" BUILD_TLS=no \
    && make -C "/usr/src/redis-${REDIS_TAG}" install

FROM debian:bookworm-slim
COPY --from=build /usr/local/bin/redis-server /usr/local/bin/redis-cli /usr/local/bin/
EXPOSE 6379
CMD ["redis-server", "--protected-mode", "no"]
