.PHONY: help bootstrap build up down logs restart clean migrate seed health health-all

help: ## Show this help message
	@echo 'Usage: make [target]'
	@echo ''
	@echo 'Available targets:'
	@awk 'BEGIN {FS = ":.*?## "} /^[a-zA-Z_-]+:.*?## / {printf "  %-15s %s\n", $$1, $$2}' $(MAKEFILE_LIST)

bootstrap: ## Initialize the project (copy .env)
	@echo "Bootstrapping RepLink..."
	@if [ ! -f .env ]; then cp .env.example .env; echo ".env file created from .env.example"; fi
	@echo "Bootstrap complete!"

build: ## Build all Docker containers
	@echo "Building all services..."
	docker-compose build

up: ## Start all services
	@echo "Starting RepLink services..."
	docker-compose up -d
	@echo "Services started! Gateway: http://localhost:8000"

up-build: ## Build and start all services
	@echo "Building and starting RepLink services..."
	docker-compose up --build -d
	@echo "Services started! Gateway: http://localhost:8000"

down: ## Stop all services
	@echo "Stopping RepLink services..."
	docker-compose down

logs: ## View logs from all services
	docker-compose logs -f

logs-gateway: ## View gateway logs
	docker-compose logs -f gateway

logs-auth: ## View auth-api logs
	docker-compose logs -f auth-api

logs-users: ## View users-api logs
	docker-compose logs -f users-api

logs-posts: ## View posts-api logs
	docker-compose logs -f posts-api

logs-feed: ## View feed-api logs
	docker-compose logs -f feed-api

logs-comments: ## View comments-api logs
	docker-compose logs -f comments-api

logs-likes: ## View likes-api logs
	docker-compose logs -f likes-api

logs-commentslikes: ## View commentslikes-api logs (legacy)
	docker-compose logs -f commentslikes-api

logs-graph: ## View socialgraph-api logs
	docker-compose logs -f socialgraph-api

logs-content: ## View content-api logs
	docker-compose logs -f content-api

logs-fitness: ## View fitness-api logs
	docker-compose logs -f fitness-api

logs-analytics: ## View analytics-api logs
	docker-compose logs -f analytics-api

health: ## Run health check script for all services
	@bash scripts/health-check.sh

health-gateway: ## Check gateway health
	@curl -s http://localhost:8000/health | python3 -m json.tool || echo "Gateway not accessible"

health-all: ## Check aggregated health status
	@curl -s http://localhost:8000/health/all | python3 -m json.tool || echo "Health endpoint not accessible"

health-docker: ## Show Docker container health status
	@docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

restart: ## Restart all services
	@echo "Restarting RepLink services..."
	docker-compose restart

clean: ## Remove all containers, volumes, and images
	@echo "Cleaning up RepLink..."
	docker-compose down -v --rmi all
	@echo "Cleanup complete!"

migrate: ## Run database migrations for all services
	@echo "Running migrations..."
	docker-compose exec auth-api dotnet ef database update || true
	docker-compose exec users-api dotnet ef database update || true
	docker-compose exec posts-api dotnet ef database update || true
	docker-compose exec commentslikes-api dotnet ef database update || true
	docker-compose exec comments-api dotnet ef database update || true
	docker-compose exec likes-api dotnet ef database update || true
	@echo "Migrations complete!"

seed: ## Seed Neo4j with sample data
	@echo "Seeding Neo4j database..."
	@docker-compose exec neo4j cypher-shell -u neo4j -p replinkneo4j "CREATE CONSTRAINT user_id IF NOT EXISTS FOR (u:User) REQUIRE u.id IS UNIQUE;"
	@echo "Neo4j seeded!"

status: ## Show status of all services
	docker-compose ps

shell-gateway: ## Open shell in gateway container
	docker-compose exec gateway /bin/sh

shell-neo4j: ## Open Neo4j cypher-shell
	docker-compose exec neo4j cypher-shell -u neo4j -p replinkneo4j

test: ## Run tests
	@echo "Running tests..."
	@echo "No tests configured yet"
