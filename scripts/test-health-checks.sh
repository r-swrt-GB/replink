#!/bin/bash

# Test all microservice health checks
echo "=========================================="
echo "Testing RepLink Microservices Health Checks"
echo "=========================================="
echo ""

services=(
    "auth:8000:auth"
    "users:8000:users"
    "content:8000:content"
    "fitness:8000:fitness"
    "graph:8000:graph"
    "feed:8000:feed"
    "analytics:8000:analytics"
)

all_healthy=true

for service_info in "${services[@]}"; do
    IFS=':' read -r name port path <<< "$service_info"

    echo "Testing $name API..."

    response=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:$port/api/$path/health)

    if [ "$response" == "200" ]; then
        echo "✓ $name API is healthy (HTTP $response)"
        curl -s http://localhost:$port/api/$path/health | jq -r '.status, .service, .database // .cache, .timestamp' | sed 's/^/  /'
    else
        echo "✗ $name API failed health check (HTTP $response)"
        all_healthy=false
    fi

    echo ""
done

echo "=========================================="
if [ "$all_healthy" = true ]; then
    echo "✓ All services are healthy!"
    exit 0
else
    echo "✗ Some services are unhealthy"
    exit 1
fi
