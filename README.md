# RepLink - Fitness Social Platform

A production-ready Instagram-like gym and fitness social platform built with .NET 8 microservices, React, Neo4j graph database, and Docker.

## Architecture

RepLink is a microservices-based application with the following components:

### Microservices

- **Identity API** - Authentication and JWT token issuance
- **Users API** - User profile management and search
- **Posts API** - Create and retrieve fitness posts with media
- **CommentsLikes API** - Social interactions on posts
- **Social-Graph API** - Follow relationships using Neo4j graph database
- **Feed API** - Personalized feed aggregation with Redis caching
- **API Gateway** - Ocelot-based routing and JWT validation

### Databases (Database per Service Pattern)

- **PostgreSQL** - Identity, Users, Posts, CommentsLikes services
- **Neo4j** - Social-Graph relationships
- **Redis** - Feed caching (optional)
- **RabbitMQ** - Event messaging (optional)

### Frontend

- **React + Vite + Tailwind CSS** - Modern, responsive web interface

## Prerequisites

- **macOS** with Docker Desktop installed
- **Docker** and **Docker Compose**
- **.NET 8 SDK** (for local development)
- **Node.js 18+** (for local frontend development)
- **Make** (included on macOS)

## Quick Start

### 1. Bootstrap the Project

```bash
make bootstrap
```

This will:

- Copy `.env.example` to `.env`
- Install frontend dependencies

### 2. Configure Environment

Edit the `.env` file if you need to customize settings. The defaults work out of the box.

### 3. Start All Services

```bash
docker-compose up --build
```

Or using Make:

```bash
make up-build
```

This single command will:

- Build all microservices
- Start PostgreSQL databases
- Start Neo4j graph database
- Start RabbitMQ and Redis
- Launch the Ocelot API Gateway
- Start the React frontend

### 4. Access the Application

- **Frontend**: http://localhost:5173
- **API Gateway**: http://localhost:8000
- **Neo4j Browser**: http://localhost:7474 (user: `neo4j`, password: `replinkneo4j`)
- **RabbitMQ Management**: http://localhost:15672 (user: `guest`, password: `guest`)

### 5. Access Service Documentation

Each service exposes Swagger UI:

- **Identity API**: http://localhost:8000/api/identity/swagger
- **Users API**: http://localhost:8000/api/users/swagger
- **Posts API**: http://localhost:8000/api/posts/swagger
- **CommentsLikes API**: http://localhost:8000/api/commentslikes/swagger
- **Social-Graph API**: http://localhost:8000/api/graph/swagger
- **Feed API**: http://localhost:8000/api/feed/swagger

## Gateway Routing

The Ocelot API Gateway routes requests as follows:

| Route                  | Downstream Service   | Authentication         |
| ---------------------- | -------------------- | ---------------------- |
| `/api/identity/*`      | identity-api:80      | No                     |
| `/api/users/*`         | users-api:80         | Yes (JWT)              |
| `/api/posts/*`         | posts-api:80         | Yes (JWT + Role claim) |
| `/api/commentslikes/*` | commentslikes-api:80 | Yes (JWT)              |
| `/api/graph/*`         | socialgraph-api:80   | Yes (JWT)              |
| `/api/feed/*`          | feed-api:80          | Yes (JWT)              |

Only port **8000** is exposed externally. All inter-service communication happens on the internal Docker network.

## User Roles

- **athlete** - Regular fitness enthusiast
- **coach** - Personal trainers and coaches
- **influencer** - Fitness influencers

## Common Tasks

### View Logs

```bash
make logs                # All services
make logs-gateway        # Gateway only
make logs-identity       # Identity API only
make logs-graph          # Social-Graph API only
```

### Stop Services

```bash
make down
```

### Restart Services

```bash
make restart
```

### Clean Everything

```bash
make clean
```

This removes all containers, volumes, and images.

### Seed Neo4j

```bash
make seed
```

This creates unique constraints on the User nodes in Neo4j.

### Run Migrations

```bash
make migrate
```

This applies Entity Framework migrations to all PostgreSQL databases.

## End-to-End Workflow

### 1. Register a New User

**POST** `http://localhost:8000/api/identity/register`

```json
{
  "email": "athlete@replink.com",
  "password": "Password123!",
  "username": "fitathlete",
  "role": "athlete"
}
```

### 2. Login

**POST** `http://localhost:8000/api/identity/login`

```json
{
  "email": "athlete@replink.com",
  "password": "Password123!"
}
```

Returns a JWT token. Use this token in the `Authorization` header as `Bearer <token>` for all subsequent requests.

### 3. Create User Profile

**POST** `http://localhost:8000/api/users/profile`

```json
{
  "displayName": "Fit Athlete",
  "bio": "Fitness enthusiast and marathon runner",
  "avatarUrl": "https://example.com/avatar.jpg"
}
```

### 4. Create a Post

**POST** `http://localhost:8000/api/posts`

```json
{
  "caption": "Just crushed leg day! 💪 #legday #fitness",
  "mediaUrl": "https://example.com/workout.jpg",
  "hashtags": ["legday", "fitness"]
}
```

### 5. Follow Another User

**POST** `http://localhost:8000/api/graph/follow/{targetUserId}`

### 6. View Your Feed

**GET** `http://localhost:8000/api/feed`

Returns posts from users you follow, cached in Redis.

### 7. Like a Post

**POST** `http://localhost:8000/api/commentslikes/likes`

```json
{
  "postId": "post-uuid-here"
}
```

### 8. Comment on a Post

**POST** `http://localhost:8000/api/commentslikes/comments`

```json
{
  "postId": "post-uuid-here",
  "content": "Great workout! Keep it up!"
}
```

## Neo4j Cypher Queries

The Social-Graph API uses the official Neo4j.Driver to execute Cypher queries. Examples:

### Follow a User

```cypher
MATCH (a:User {id: $userId}), (b:User {id: $targetUserId})
MERGE (a)-[:FOLLOWS]->(b)
```

### Get Followers

```cypher
MATCH (follower:User)-[:FOLLOWS]->(u:User {id: $userId})
RETURN follower
```

### Get Following

```cypher
MATCH (u:User {id: $userId})-[:FOLLOWS]->(following:User)
RETURN following
```

### Get Recommendations

```cypher
MATCH (u:User {id: $userId})-[:FOLLOWS]->()-[:FOLLOWS]->(recommended:User)
WHERE NOT (u)-[:FOLLOWS]->(recommended) AND u <> recommended
RETURN recommended, COUNT(*) as mutualConnections
ORDER BY mutualConnections DESC
LIMIT 10
```

### Get Feed Sources

```cypher
MATCH (u:User {id: $userId})-[:FOLLOWS]->(followed:User)
RETURN followed.id as userId
```

## Development

### Run Frontend Locally (outside Docker)

```bash
make dev-frontend
```

Or:

```bash
cd frontend/web
npm run dev
```

### Access Neo4j Cypher Shell

```bash
make shell-neo4j
```

### Access Gateway Container Shell

```bash
make shell-gateway
```

## Project Structure

```
replink/
├── docker-compose.yml          # Docker Compose orchestration
├── .env.example                # Environment variables template
├── Makefile                    # Helper commands
├── README.md                   # This file
├── RepLink.sln                 # .NET solution file
├── global.json                 # .NET SDK version
├── gateway/
│   └── OcelotApiGw/           # API Gateway
│       ├── Dockerfile
│       ├── Program.cs
│       ├── ocelot.json        # Gateway routing configuration
│       └── OcelotApiGw.csproj
├── services/
│   ├── identity-api/          # Authentication service
│   ├── users-api/             # User profiles service
│   ├── posts-api/             # Posts service
│   ├── commentslikes-api/     # Social interactions service
│   ├── socialgraph-api/       # Neo4j graph service
│   └── feed-api/              # Feed aggregation service
└── frontend/
    └── web/                   # React frontend
        ├── Dockerfile
        ├── package.json
        ├── vite.config.js
        └── src/
```

## Health Checks

All services expose a `/health` endpoint for monitoring:

- http://localhost:8000/api/identity/health
- http://localhost:8000/api/users/health
- http://localhost:8000/api/posts/health
- http://localhost:8000/api/commentslikes/health
- http://localhost:8000/api/graph/health
- http://localhost:8000/api/feed/health

## Logging

All services use **Serilog** for structured logging with console output. Logs are visible via:

```bash
docker-compose logs -f <service-name>
```

## Troubleshooting

### Services Not Starting

Check that Docker Desktop is running and has enough resources allocated (at least 4GB RAM recommended).

### Database Connection Errors

Ensure databases are healthy:

```bash
docker-compose ps
```

All services should show "healthy" status.

### JWT Authentication Errors

Verify that `JWT_SECRET` is the same across all services in your `.env` file.

### Neo4j Connection Issues

Wait for Neo4j to fully start (can take 30-60 seconds on first run). Check logs:

```bash
docker-compose logs neo4j
```

## Contributing

This is a starter template. Extend it with:

- Unit and integration tests
- CI/CD pipelines
- Kubernetes manifests
- Monitoring and observability (Prometheus, Grafana)
- API rate limiting
- Image upload to cloud storage
- Real-time notifications with SignalR
- Direct messaging

## License

MIT License - feel free to use for your projects!

## Support

For issues and questions, refer to the official documentation:

- [ASP.NET Core](https://docs.microsoft.com/aspnet/core)
- [Ocelot](https://ocelot.readthedocs.io)
- [Neo4j .NET Driver](https://neo4j.com/docs/dotnet-manual/current/)
- [React](https://react.dev)
- [Docker](https://docs.docker.com)
