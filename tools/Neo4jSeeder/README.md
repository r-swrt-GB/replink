# RepLink Neo4j Database Seeder

A comprehensive database seeding tool for the RepLink fitness social platform. This tool populates your Neo4j database with realistic test data including users, exercises, clubs, workouts, posts, comments, likes, and social relationships.

## Features

- Creates realistic test data using the Bogus library
- Establishes proper Neo4j constraints and indexes
- Seeds connected nodes with realistic relationships
- Supports configurable quantities for all data types
- Includes data verification and statistics

## Data Created

### Nodes
- **50 Users** (AuthUser + UserProfile pairs)
- **40 Exercises** (Strength, Cardio, Flexibility, Functional)
- **15 Clubs** (Gyms and fitness centers)
- **100 Workouts** (User-created workout plans)
- **200 Posts** (Social media posts with hashtags)
- **400 Comments** (Comments on posts)
- **800 Likes** (Likes on posts)

### Relationships
- **~150 FOLLOWS** relationships (user follows user)
- **~80 TRAINS_AT** relationships (user trains at club)
- **~50 PERFORMS** relationships (user performs workout)
- **~400 CONTAINS** relationships (workout contains exercises)

## Prerequisites

- .NET 8.0 SDK
- Neo4j database running (local or Docker)
- Neo4j connection details:
  - URI: `bolt://localhost:7687`
  - Username: `neo4j`
  - Password: `replinkneo4j`

## Installation

1. Navigate to the seeder directory:
```bash
cd tools/Neo4jSeeder/Neo4jSeeder
```

2. Restore dependencies:
```bash
dotnet restore
```

## Usage

### Option 1: Run directly
```bash
dotnet run
```

### Option 2: Build and run
```bash
dotnet build
dotnet run --no-build
```

### Option 3: Publish and execute
```bash
dotnet publish -c Release
cd bin/Release/net8.0/publish
./Neo4jSeeder
```

## Configuration

You can modify the seeding quantities by editing the constants in `Program.cs`:

```csharp
private const int NUM_USERS = 50;
private const int NUM_EXERCISES = 40;
private const int NUM_CLUBS = 15;
private const int NUM_WORKOUTS = 100;
private const int NUM_POSTS = 200;
private const int NUM_COMMENTS = 400;
private const int NUM_LIKES = 800;
private const int NUM_FOLLOWS = 150;
private const int NUM_TRAINS_AT = 80;
```

To change the Neo4j connection details, modify these constants:

```csharp
private const string NEO4J_URI = "bolt://localhost:7687";
private const string NEO4J_USER = "neo4j";
private const string NEO4J_PASSWORD = "replinkneo4j";
```

## Running with Docker

If you're running Neo4j in Docker (via docker-compose), make sure the database is running first:

```bash
# From the project root
docker-compose up neo4j -d

# Wait for Neo4j to be ready (check logs)
docker-compose logs -f neo4j

# Once ready, run the seeder
cd tools/Neo4jSeeder/Neo4jSeeder
dotnet run
```

## What Gets Seeded

### 1. Users
- Realistic usernames, emails, and display names
- Hashed passwords (all users have password: `Password123!`)
- Random roles: athlete, coach, or influencer
- Unique email and username constraints
- Paired AuthUser and UserProfile nodes

### 2. Exercises
40 exercises across categories:
- **Strength**: Bench Press, Deadlift, Squats, Pull-ups, etc.
- **Cardio**: Running, Cycling, Swimming, Rowing, etc.
- **Core**: Planks, Crunches, Russian Twists, etc.
- **Flexibility**: Yoga, Stretching, Foam Rolling
- **Functional**: Burpees, Box Jumps, Kettlebell Swings

### 3. Clubs
15 fitness clubs with:
- Realistic gym names
- Random locations (city, state)
- Motivational descriptions

### 4. Workouts
100 user-created workouts with:
- Titles like "Leg Day Domination", "Chest Day Crusher"
- 2-6 exercises per workout
- Duration between 20-120 minutes
- Linked to creators via userId

### 5. Posts
200 social media posts featuring:
- Motivational captions
- Random hashtags (#fitness, #gains, #gymlife, etc.)
- Links to 0-3 exercises
- Random media URLs

### 6. Social Engagement
- **Comments**: Encouraging messages on posts
- **Likes**: Unique user-post combinations
- **Follows**: Realistic social network (users can't follow themselves)

### 7. Relationships
- **TRAINS_AT**: Users training at specific clubs
- **PERFORMS**: Users performing workouts they created
- **CONTAINS**: Workouts containing specific exercises with sets/reps

## Output Example

```
╔════════════════════════════════════════════╗
║   RepLink Neo4j Database Seeder v1.0      ║
╚════════════════════════════════════════════╝

✓ Connected to Neo4j

→ Creating database constraints...
→ Clearing existing data... Done
→ Creating 50 users... Done (50 created)
→ Creating 40 exercises... Done (40 created)
→ Creating 15 clubs... Done (15 created)
→ Creating 100 workouts... Done (100 created)
→ Creating 200 posts... Done (200 created)
→ Creating 400 comments... Done (400 created)
→ Creating 800 likes... Done (756 created)
→ Creating 150 follow relationships... Done (147 created)
→ Creating 80 trains-at relationships... Done (80 created)
→ Creating workout-exercise relationships... Done (450 created)
→ Creating user-workout relationships... Done (50 created)

→ Verifying data...
  • Users: 50
  • Profiles: 50
  • Exercises: 40
  • Clubs: 15
  • Workouts: 100
  • Posts: 200
  • Comments: 400
  • Likes: 756

  • FOLLOWS: 147
  • TRAINS_AT: 80
  • PERFORMS: 50
  • CONTAINS: 450

✓ Database seeding completed successfully!
```

## Important Notes

### Data Clearing
By default, the seeder **clears all existing data** before seeding. If you want to preserve existing data, comment out line 80 in `Program.cs`:

```csharp
// await ClearDatabase();  // Comment this out to keep existing data
```

### Password Security
All seeded users have the same password: `Password123!` (hashed with BCrypt). This is for testing only and should not be used in production.

### Relationship Uniqueness
The seeder ensures:
- Users cannot follow themselves
- No duplicate likes (same user + post)
- No duplicate follows (same follower + followed)
- No duplicate club memberships

### Timestamps
All data is timestamped with realistic dates:
- Exercises: 1-2 years old
- Clubs: 1-3 years old
- Users: Up to 1 year old
- Posts/Workouts: Last 6 months
- Comments/Likes: Last 5 months

## Troubleshooting

### Connection Refused
If you get a connection error:
1. Ensure Neo4j is running: `docker-compose ps`
2. Check Neo4j logs: `docker-compose logs neo4j`
3. Verify port 7687 is accessible
4. Confirm credentials match your `.env` file

### Constraint Violations
If you see constraint violation errors:
1. Clear the database manually via Neo4j Browser
2. Run: `MATCH (n) DETACH DELETE n`
3. Re-run the seeder

### Slow Performance
If seeding is slow:
1. Reduce the quantity constants
2. Ensure Neo4j has enough memory
3. Check Docker container resources

## Verifying Data in Neo4j Browser

After seeding, you can verify the data in Neo4j Browser (http://localhost:7474):

```cypher
// View all node types and counts
MATCH (n) RETURN labels(n) as type, count(*) as count

// View all relationships
MATCH ()-[r]->() RETURN type(r) as relationship, count(*) as count

// Find users with the most followers
MATCH (u:AuthUser)<-[f:FOLLOWS]-()
RETURN u.username, count(f) as followers
ORDER BY followers DESC
LIMIT 10

// Find most liked posts
MATCH (p:Post)<-[l:LIKE]-()
RETURN p.caption, count(l) as likes
ORDER BY likes DESC
LIMIT 10

// View workout with exercises
MATCH (w:Workout)-[c:CONTAINS]->(e:Exercise)
WHERE w.title = "Chest Day Crusher"
RETURN w, c, e
```

## Dependencies

- **Neo4j.Driver** (5.28.3): Official Neo4j driver for .NET
- **Bogus** (35.6.4): Fake data generator
- **BCrypt.Net-Next** (4.0.3): Password hashing

## License

Part of the RepLink project.
