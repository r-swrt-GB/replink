#!/bin/bash

# Health Check Monitoring Script for RepLink Microservices
# This script checks the health of all services and displays their status

set -e

GATEWAY_URL="http://localhost:8000"
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo "======================================"
echo "RepLink Health Check Monitor"
echo "======================================"
echo ""

# Function to check individual service health
check_service() {
    local service_name=$1
    local service_url=$2

    echo -n "Checking $service_name... "

    response=$(curl -s -o /dev/null -w "%{http_code}" "$service_url" 2>/dev/null || echo "000")

    if [ "$response" == "200" ]; then
        echo -e "${GREEN}✓ Healthy${NC}"
        return 0
    else
        echo -e "${RED}✗ Unhealthy (HTTP $response)${NC}"
        return 1
    fi
}

# Function to check Docker container health
check_container_health() {
    local container_name=$1

    echo -n "Container $container_name... "

    if ! docker ps --format '{{.Names}}' | grep -q "^${container_name}$"; then
        echo -e "${RED}✗ Not Running${NC}"
        return 1
    fi

    health_status=$(docker inspect --format='{{.State.Health.Status}}' "$container_name" 2>/dev/null || echo "none")

    case "$health_status" in
        "healthy")
            echo -e "${GREEN}✓ Healthy${NC}"
            return 0
            ;;
        "unhealthy")
            echo -e "${RED}✗ Unhealthy${NC}"
            return 1
            ;;
        "starting")
            echo -e "${YELLOW}⚠ Starting${NC}"
            return 2
            ;;
        "none")
            echo -e "${YELLOW}⚠ No health check${NC}"
            return 2
            ;;
        *)
            echo -e "${RED}✗ Unknown status${NC}"
            return 1
            ;;
    esac
}

# Check if services are running
echo "=== Docker Container Status ==="
check_container_health "replink-neo4j"
check_container_health "replink-redis"
check_container_health "replink-rabbitmq"
echo ""
check_container_health "replink-auth-api"
check_container_health "replink-users-api"
check_container_health "replink-content-api"
check_container_health "replink-fitness-api"
check_container_health "replink-socialgraph-api"
check_container_health "replink-feed-api"
check_container_health "replink-analytics-api"
check_container_health "replink-gateway"
echo ""

# Check service HTTP endpoints
echo "=== Service HTTP Health Endpoints ==="
check_service "Auth API" "$GATEWAY_URL/api/auth/health"
check_service "Users API" "$GATEWAY_URL/api/users/health"
check_service "Content API" "$GATEWAY_URL/api/content/health"
check_service "Fitness API" "$GATEWAY_URL/api/fitness/health"
check_service "Social Graph API" "$GATEWAY_URL/api/graph/health"
check_service "Feed API" "$GATEWAY_URL/api/feed/health"
check_service "Analytics API" "$GATEWAY_URL/api/analytics/health"
echo ""

# Check aggregated health endpoint
echo "=== Aggregated Health Status ==="
if curl -s "$GATEWAY_URL/health/all" > /tmp/health-all.json 2>/dev/null; then
    overall_status=$(cat /tmp/health-all.json | grep -o '"overallStatus":"[^"]*"' | cut -d'"' -f4)

    if [ "$overall_status" == "Healthy" ]; then
        echo -e "${GREEN}✓ All Services Healthy${NC}"
    else
        echo -e "${YELLOW}⚠ System Status: $overall_status${NC}"
        echo ""
        echo "Service Details:"
        cat /tmp/health-all.json | python3 -m json.tool 2>/dev/null || cat /tmp/health-all.json
    fi
    rm -f /tmp/health-all.json
else
    echo -e "${RED}✗ Failed to fetch aggregated health status${NC}"
fi

echo ""
echo "======================================"
echo "Health check complete!"
echo "======================================"
