using Neo4j.Driver;
using Bogus;
using System.Text.Json;

namespace Neo4jSeeder;

class Program
{
    private static IDriver? _driver;
    private static readonly Random _random = new();

    // Configuration
    private const string NEO4J_URI = "bolt://localhost:7687";
    private const string NEO4J_USER = "neo4j";
    private const string NEO4J_PASSWORD = "replinkneo4j";

    // Seeding quantities
    private const int NUM_USERS = 50;
    private const int NUM_EXERCISES = 40;
    private const int NUM_CLUBS = 15;
    private const int NUM_WORKOUTS = 100;
    private const int NUM_POSTS = 200;
    private const int NUM_COMMENTS = 400;
    private const int NUM_LIKES = 800;
    private const int NUM_FOLLOWS = 150;
    private const int NUM_TRAINS_AT = 80;

    static async Task Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════════╗");
        Console.WriteLine("║   RepLink Neo4j Database Seeder v1.0      ║");
        Console.WriteLine("╚════════════════════════════════════════════╝");
        Console.WriteLine();

        try
        {
            // Connect to Neo4j
            _driver = GraphDatabase.Driver(NEO4J_URI, AuthTokens.Basic(NEO4J_USER, NEO4J_PASSWORD));
            await VerifyConnection();

            Console.WriteLine("✓ Connected to Neo4j");
            Console.WriteLine();

            // Seed data
            await SeedData();

            // Verify data
            await VerifyData();

            Console.WriteLine();
            Console.WriteLine("✓ Database seeding completed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            if (_driver != null)
            {
                await _driver.DisposeAsync();
            }
        }
    }

    static async Task VerifyConnection()
    {
        await _driver!.VerifyConnectivityAsync();
    }

    static async Task SeedData()
    {
        // Step 1: Create constraints (idempotent)
        Console.WriteLine("→ Creating database constraints...");
        await CreateConstraints();

        // Step 2: Clear existing data (optional - comment out if you want to keep existing data)
        Console.Write("→ Clearing existing data... ");
        await ClearDatabase();
        Console.WriteLine("Done");

        // Step 3: Seed base data
        Console.Write($"→ Creating {NUM_USERS} users... ");
        var userIds = await SeedUsers();
        Console.WriteLine($"Done ({userIds.Count} created)");

        Console.Write($"→ Creating {NUM_EXERCISES} exercises... ");
        var exerciseIds = await SeedExercises();
        Console.WriteLine($"Done ({exerciseIds.Count} created)");

        Console.Write($"→ Creating {NUM_CLUBS} clubs... ");
        var clubIds = await SeedClubs();
        Console.WriteLine($"Done ({clubIds.Count} created)");

        // Step 4: Seed user-generated content
        Console.Write($"→ Creating {NUM_WORKOUTS} workouts... ");
        var workoutIds = await SeedWorkouts(userIds, exerciseIds);
        Console.WriteLine($"Done ({workoutIds.Count} created)");

        Console.Write($"→ Creating {NUM_POSTS} posts... ");
        var postIds = await SeedPosts(userIds, exerciseIds);
        Console.WriteLine($"Done ({postIds.Count} created)");

        Console.Write($"→ Creating {NUM_COMMENTS} comments... ");
        var commentCount = await SeedComments(userIds, postIds);
        Console.WriteLine($"Done ({commentCount} created)");

        Console.Write($"→ Creating {NUM_LIKES} likes... ");
        var likeCount = await SeedLikes(userIds, postIds);
        Console.WriteLine($"Done ({likeCount} created)");

        // Step 5: Seed relationships
        Console.Write($"→ Creating {NUM_FOLLOWS} follow relationships... ");
        var followCount = await SeedFollows(userIds);
        Console.WriteLine($"Done ({followCount} created)");

        Console.Write($"→ Creating {NUM_TRAINS_AT} trains-at relationships... ");
        var trainsAtCount = await SeedTrainsAt(userIds, clubIds);
        Console.WriteLine($"Done ({trainsAtCount} created)");

        Console.Write("→ Creating workout-exercise relationships... ");
        var containsCount = await SeedWorkoutExercises(workoutIds, exerciseIds);
        Console.WriteLine($"Done ({containsCount} created)");

        Console.Write("→ Creating user-workout relationships... ");
        var performsCount = await SeedPerforms(userIds, workoutIds);
        Console.WriteLine($"Done ({performsCount} created)");
    }

    static async Task CreateConstraints()
    {
        var session = _driver!.AsyncSession();
        try
        {
            // AuthUser constraints
            await session.RunAsync("CREATE CONSTRAINT auth_user_id_unique IF NOT EXISTS FOR (u:AuthUser) REQUIRE u.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT auth_user_email_unique IF NOT EXISTS FOR (u:AuthUser) REQUIRE u.email IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT auth_user_username_unique IF NOT EXISTS FOR (u:AuthUser) REQUIRE u.username IS UNIQUE");

            // UserProfile constraints
            await session.RunAsync("CREATE CONSTRAINT userprofile_userid_unique IF NOT EXISTS FOR (up:UserProfile) REQUIRE up.userId IS UNIQUE");

            // Exercise, Club, Workout, Post, Comment, Like constraints
            await session.RunAsync("CREATE CONSTRAINT exercise_id_unique IF NOT EXISTS FOR (e:Exercise) REQUIRE e.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT club_id_unique IF NOT EXISTS FOR (c:Club) REQUIRE c.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT workout_id_unique IF NOT EXISTS FOR (w:Workout) REQUIRE w.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT post_id_unique IF NOT EXISTS FOR (p:Post) REQUIRE p.id IS UNIQUE");
            await session.RunAsync("CREATE CONSTRAINT comment_id_unique IF NOT EXISTS FOR (c:Comment) REQUIRE c.id IS UNIQUE");
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    static async Task ClearDatabase()
    {
        var session = _driver!.AsyncSession();
        try
        {
            // Delete all relationships and nodes
            await session.RunAsync("MATCH (n) DETACH DELETE n");
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    static async Task<List<string>> SeedUsers()
    {
        var userIds = new List<string>();
        var faker = new Faker();
        var session = _driver!.AsyncSession();

        try
        {
            for (int i = 0; i < NUM_USERS; i++)
            {
                var userId = Guid.NewGuid().ToString();
                var username = faker.Internet.UserName() + i; // Make unique
                var email = faker.Internet.Email(username);
                var displayName = faker.Name.FullName();
                var bio = faker.Lorem.Sentence(10);
                var avatarUrl = faker.Internet.Avatar();
                var role = faker.PickRandom("athlete", "coach", "influencer");
                var createdAt = DateTime.UtcNow.AddDays(-_random.Next(1, 365)).ToString("o");

                // Create AuthUser and UserProfile
                await session.RunAsync(@"
                    CREATE (u:AuthUser {
                        id: $userId,
                        email: $email,
                        username: $username,
                        passwordHash: $passwordHash,
                        role: $role,
                        createdAt: $createdAt
                    })
                    CREATE (p:UserProfile {
                        id: $profileId,
                        userId: $userId,
                        displayName: $displayName,
                        bio: $bio,
                        avatarUrl: $avatarUrl,
                        createdAt: $createdAt
                    })",
                    new
                    {
                        userId,
                        email,
                        username,
                        passwordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
                        role,
                        createdAt,
                        profileId = Guid.NewGuid().ToString(),
                        displayName,
                        bio,
                        avatarUrl
                    });

                userIds.Add(userId);
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return userIds;
    }

    static async Task<List<string>> SeedExercises()
    {
        var exerciseIds = new List<string>();
        var session = _driver!.AsyncSession();

        var exercises = new[]
        {
            // Strength - Chest
            ("Bench Press", "Strength", "Chest", "Classic barbell bench press for chest development"),
            ("Dumbbell Flyes", "Strength", "Chest", "Isolation exercise for chest muscles"),
            ("Push-ups", "Strength", "Chest", "Bodyweight chest exercise"),
            ("Incline Bench Press", "Strength", "Chest", "Targets upper chest muscles"),
            ("Cable Crossover", "Strength", "Chest", "Cable chest isolation exercise"),

            // Strength - Back
            ("Deadlift", "Strength", "Back", "Compound exercise for entire posterior chain"),
            ("Pull-ups", "Strength", "Back", "Bodyweight back exercise"),
            ("Bent-Over Row", "Strength", "Back", "Barbell rowing for back thickness"),
            ("Lat Pulldown", "Strength", "Back", "Cable exercise for lats"),
            ("T-Bar Row", "Strength", "Back", "Rowing variation for back development"),

            // Strength - Legs
            ("Squats", "Strength", "Legs", "King of leg exercises"),
            ("Leg Press", "Strength", "Legs", "Machine-based leg exercise"),
            ("Lunges", "Strength", "Legs", "Single-leg strength exercise"),
            ("Romanian Deadlift", "Strength", "Legs", "Hamstring-focused deadlift variation"),
            ("Leg Curls", "Strength", "Legs", "Isolation exercise for hamstrings"),
            ("Leg Extensions", "Strength", "Legs", "Isolation exercise for quadriceps"),

            // Strength - Shoulders
            ("Overhead Press", "Strength", "Shoulders", "Compound shoulder exercise"),
            ("Lateral Raises", "Strength", "Shoulders", "Isolation for side delts"),
            ("Front Raises", "Strength", "Shoulders", "Isolation for front delts"),
            ("Face Pulls", "Strength", "Shoulders", "Rear delt and upper back exercise"),

            // Strength - Arms
            ("Barbell Curl", "Strength", "Biceps", "Classic bicep exercise"),
            ("Hammer Curl", "Strength", "Biceps", "Neutral grip bicep curl"),
            ("Tricep Dips", "Strength", "Triceps", "Compound tricep exercise"),
            ("Tricep Pushdown", "Strength", "Triceps", "Cable tricep isolation"),
            ("Close-Grip Bench Press", "Strength", "Triceps", "Bench press variation for triceps"),

            // Strength - Core
            ("Plank", "Core", "Abs", "Isometric core exercise"),
            ("Crunches", "Core", "Abs", "Classic ab exercise"),
            ("Russian Twist", "Core", "Abs", "Rotational core exercise"),
            ("Hanging Leg Raises", "Core", "Abs", "Advanced ab exercise"),

            // Cardio
            ("Running", "Cardio", "Full Body", "Outdoor or treadmill running"),
            ("Cycling", "Cardio", "Legs", "Indoor or outdoor cycling"),
            ("Rowing", "Cardio", "Full Body", "Full body cardio exercise"),
            ("Jump Rope", "Cardio", "Full Body", "High-intensity cardio"),
            ("Swimming", "Cardio", "Full Body", "Low-impact full body cardio"),

            // Flexibility
            ("Yoga Flow", "Flexibility", "Full Body", "Dynamic yoga routine"),
            ("Static Stretching", "Flexibility", "Full Body", "Post-workout stretching"),
            ("Foam Rolling", "Recovery", "Full Body", "Self-myofascial release"),

            // Functional
            ("Burpees", "Functional", "Full Body", "Full body conditioning exercise"),
            ("Box Jumps", "Plyometric", "Legs", "Explosive leg exercise"),
            ("Kettlebell Swings", "Functional", "Full Body", "Dynamic hip hinge exercise")
        };

        try
        {
            foreach (var (name, category, muscleGroup, description) in exercises)
            {
                var exerciseId = Guid.NewGuid().ToString();
                var createdAt = DateTime.UtcNow.AddDays(-_random.Next(365, 730)).ToString("o");

                await session.RunAsync(@"
                    CREATE (e:Exercise {
                        id: $id,
                        name: $name,
                        description: $description,
                        category: $category,
                        muscleGroup: $muscleGroup,
                        mediaUrl: $mediaUrl,
                        createdAt: $createdAt,
                        createdBy: 'system'
                    })",
                    new
                    {
                        id = exerciseId,
                        name,
                        description,
                        category,
                        muscleGroup,
                        mediaUrl = $"https://example.com/exercises/{name.ToLower().Replace(" ", "-")}.jpg",
                        createdAt
                    });

                exerciseIds.Add(exerciseId);
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return exerciseIds;
    }

    static async Task<List<string>> SeedClubs()
    {
        var clubIds = new List<string>();
        var faker = new Faker();
        var session = _driver!.AsyncSession();

        var clubNames = new[]
        {
            "Iron Paradise Gym", "FitLife Health Club", "CrossFit Champions",
            "PowerHouse Fitness", "Yoga Sanctuary", "Athletic Performance Center",
            "Gold's Gym Downtown", "24/7 Fitness Hub", "Elite Training Facility",
            "Community Wellness Center", "Warrior Gym", "Flex Fitness Studio",
            "Peak Performance Gym", "Total Body Transformation", "Urban Athlete"
        };

        try
        {
            foreach (var name in clubNames)
            {
                var clubId = Guid.NewGuid().ToString();
                var createdAt = DateTime.UtcNow.AddDays(-_random.Next(365, 1095)).ToString("o");

                await session.RunAsync(@"
                    CREATE (c:Club {
                        id: $id,
                        name: $name,
                        description: $description,
                        location: $location,
                        createdAt: $createdAt,
                        createdBy: 'system'
                    })",
                    new
                    {
                        id = clubId,
                        name,
                        description = faker.Company.CatchPhrase(),
                        location = $"{faker.Address.City()}, {faker.Address.StateAbbr()}",
                        createdAt
                    });

                clubIds.Add(clubId);
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return clubIds;
    }

    static async Task<List<string>> SeedWorkouts(List<string> userIds, List<string> exerciseIds)
    {
        var workoutIds = new List<string>();
        var faker = new Faker();
        var session = _driver!.AsyncSession();

        var workoutTitles = new[]
        {
            "Full Body Blast", "Chest Day Crusher", "Leg Day Domination", "Back Attack",
            "Shoulder Shredder", "Arm Annihilation", "Core Conditioning", "HIIT Circuit",
            "Strength Builder", "Endurance Challenge", "Power Workout", "Functional Fitness",
            "Morning Mobility", "Evening Pump", "Weekend Warrior", "Quick 30", "Beast Mode",
            "Total Body Transformation", "Athletic Performance", "Muscle Building"
        };

        try
        {
            for (int i = 0; i < NUM_WORKOUTS; i++)
            {
                var workoutId = Guid.NewGuid().ToString();
                var userId = userIds[_random.Next(userIds.Count)];
                var title = faker.PickRandom(workoutTitles);
                var createdAt = DateTime.UtcNow.AddDays(-_random.Next(1, 180)).ToString("o");

                // Select 2-6 random exercises for this workout
                var selectedExercises = exerciseIds.OrderBy(x => _random.Next()).Take(_random.Next(2, 7)).ToList();

                await session.RunAsync(@"
                    CREATE (w:Workout {
                        id: $id,
                        userId: $userId,
                        title: $title,
                        description: $description,
                        exerciseIds: $exerciseIds,
                        durationMinutes: $durationMinutes,
                        createdAt: $createdAt
                    })",
                    new
                    {
                        id = workoutId,
                        userId,
                        title,
                        description = faker.Lorem.Sentence(),
                        exerciseIds = selectedExercises,
                        durationMinutes = _random.Next(20, 120),
                        createdAt
                    });

                workoutIds.Add(workoutId);
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return workoutIds;
    }

    static async Task<List<string>> SeedPosts(List<string> userIds, List<string> exerciseIds)
    {
        var postIds = new List<string>();
        var faker = new Faker();
        var session = _driver!.AsyncSession();

        var captions = new[]
        {
            "Crushing it today! 💪", "New PR! Feeling strong 🔥", "Leg day done ✓",
            "Morning workout complete!", "Progress over perfection", "No pain, no gain",
            "Fitness is a journey, not a destination", "Earned this rest day",
            "Consistency is key 🔑", "Mind over matter", "One rep at a time",
            "Beast mode activated", "Training like a champion", "Sweat is just fat crying"
        };

        var hashtags = new[]
        {
            "fitness", "workout", "gym", "training", "health", "motivation",
            "bodybuilding", "strength", "gains", "fitfam", "gymlife", "fitnessmotivation",
            "exercise", "healthy", "muscle", "fit", "cardio", "lifestyle"
        };

        try
        {
            for (int i = 0; i < NUM_POSTS; i++)
            {
                var postId = Guid.NewGuid().ToString();
                var userId = userIds[_random.Next(userIds.Count)];
                var createdAt = DateTime.UtcNow.AddDays(-_random.Next(1, 180)).ToString("o");

                // Select 0-3 random exercises for this post
                var selectedExercises = exerciseIds.OrderBy(x => _random.Next()).Take(_random.Next(0, 4)).ToList();

                // Select 2-5 random hashtags
                var selectedHashtags = hashtags.OrderBy(x => _random.Next()).Take(_random.Next(2, 6)).ToList();

                await session.RunAsync(@"
                    CREATE (p:Post {
                        id: $id,
                        userId: $userId,
                        caption: $caption,
                        mediaUrl: $mediaUrl,
                        hashtags: $hashtags,
                        exerciseIds: $exerciseIds,
                        createdAt: $createdAt,
                        commentsCount: 0
                    })",
                    new
                    {
                        id = postId,
                        userId,
                        caption = faker.PickRandom(captions),
                        mediaUrl = faker.Image.PicsumUrl(),
                        hashtags = selectedHashtags,
                        exerciseIds = selectedExercises,
                        createdAt
                    });

                postIds.Add(postId);
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return postIds;
    }

    static async Task<int> SeedComments(List<string> userIds, List<string> postIds)
    {
        var faker = new Faker();
        var session = _driver!.AsyncSession();
        var count = 0;

        var comments = new[]
        {
            "Great work!", "Keep it up!", "Awesome!", "So inspiring!",
            "You're killing it!", "Goals!", "Amazing progress!",
            "This is the way", "Let's gooo!", "Respect 💪",
            "Keep pushing!", "Impressive!", "Love this!", "Well done!",
            "You got this!", "Beast mode!", "Incredible!", "Nice!"
        };

        try
        {
            for (int i = 0; i < NUM_COMMENTS; i++)
            {
                var commentId = Guid.NewGuid().ToString();
                var postId = postIds[_random.Next(postIds.Count)];
                var userId = userIds[_random.Next(userIds.Count)];
                var createdAt = DateTime.UtcNow.AddDays(-_random.Next(1, 150)).ToString("o");

                await session.RunAsync(@"
                    CREATE (c:Comment {
                        id: $id,
                        postId: $postId,
                        userId: $userId,
                        content: $content,
                        createdAt: $createdAt
                    })",
                    new
                    {
                        id = commentId,
                        postId,
                        userId,
                        content = faker.PickRandom(comments),
                        createdAt
                    });

                count++;
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return count;
    }

    static async Task<int> SeedLikes(List<string> userIds, List<string> postIds)
    {
        var session = _driver!.AsyncSession();
        var count = 0;
        var existingLikes = new HashSet<string>();

        try
        {
            for (int i = 0; i < NUM_LIKES; i++)
            {
                var postId = postIds[_random.Next(postIds.Count)];
                var userId = userIds[_random.Next(userIds.Count)];
                var key = $"{postId}:{userId}";

                // Ensure uniqueness
                if (existingLikes.Contains(key)) continue;

                var likeId = Guid.NewGuid().ToString();
                var createdAt = DateTime.UtcNow.AddDays(-_random.Next(1, 150)).ToString("o");

                await session.RunAsync(@"
                    CREATE (l:Like {
                        id: $id,
                        postId: $postId,
                        userId: $userId,
                        createdAt: $createdAt
                    })",
                    new
                    {
                        id = likeId,
                        postId,
                        userId,
                        createdAt
                    });

                existingLikes.Add(key);
                count++;
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return count;
    }

    static async Task<int> SeedFollows(List<string> userIds)
    {
        var session = _driver!.AsyncSession();
        var count = 0;
        var existingFollows = new HashSet<string>();

        try
        {
            for (int i = 0; i < NUM_FOLLOWS; i++)
            {
                var followerId = userIds[_random.Next(userIds.Count)];
                var followedId = userIds[_random.Next(userIds.Count)];

                // Can't follow yourself
                if (followerId == followedId) continue;

                var key = $"{followerId}:{followedId}";
                if (existingFollows.Contains(key)) continue;

                await session.RunAsync(@"
                    MATCH (follower:AuthUser {id: $followerId})
                    MATCH (followed:AuthUser {id: $followedId})
                    CREATE (follower)-[:FOLLOWS {createdAt: $createdAt}]->(followed)",
                    new
                    {
                        followerId,
                        followedId,
                        createdAt = DateTime.UtcNow.AddDays(-_random.Next(1, 300)).ToString("o")
                    });

                existingFollows.Add(key);
                count++;
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return count;
    }

    static async Task<int> SeedTrainsAt(List<string> userIds, List<string> clubIds)
    {
        var session = _driver!.AsyncSession();
        var count = 0;
        var existingRelations = new HashSet<string>();

        try
        {
            for (int i = 0; i < NUM_TRAINS_AT; i++)
            {
                var userId = userIds[_random.Next(userIds.Count)];
                var clubId = clubIds[_random.Next(clubIds.Count)];
                var key = $"{userId}:{clubId}";

                if (existingRelations.Contains(key)) continue;

                await session.RunAsync(@"
                    MATCH (u:AuthUser {id: $userId})
                    MATCH (c:Club {id: $clubId})
                    CREATE (u)-[:TRAINS_AT {joinedAt: $joinedAt}]->(c)",
                    new
                    {
                        userId,
                        clubId,
                        joinedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 365)).ToString("o")
                    });

                existingRelations.Add(key);
                count++;
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return count;
    }

    static async Task<int> SeedWorkoutExercises(List<string> workoutIds, List<string> exerciseIds)
    {
        var session = _driver!.AsyncSession();
        var count = 0;

        try
        {
            foreach (var workoutId in workoutIds)
            {
                // Each workout contains 2-6 exercises
                var numExercises = _random.Next(2, 7);
                var selectedExercises = exerciseIds.OrderBy(x => _random.Next()).Take(numExercises);

                foreach (var exerciseId in selectedExercises)
                {
                    await session.RunAsync(@"
                        MATCH (w:Workout {id: $workoutId})
                        MATCH (e:Exercise {id: $exerciseId})
                        CREATE (w)-[:CONTAINS {order: $order, sets: $sets, reps: $reps}]->(e)",
                        new
                        {
                            workoutId,
                            exerciseId,
                            order = count % numExercises,
                            sets = _random.Next(2, 6),
                            reps = _random.Next(6, 16)
                        });

                    count++;
                }
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return count;
    }

    static async Task<int> SeedPerforms(List<string> userIds, List<string> workoutIds)
    {
        var session = _driver!.AsyncSession();
        var count = 0;

        try
        {
            // Some workouts are performed by their creators
            foreach (var workoutId in workoutIds.Take(workoutIds.Count / 2))
            {
                await session.RunAsync(@"
                    MATCH (w:Workout {id: $workoutId})
                    MATCH (u:AuthUser {id: w.userId})
                    CREATE (u)-[:PERFORMS {performedAt: $performedAt}]->(w)",
                    new
                    {
                        workoutId,
                        performedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 180)).ToString("o")
                    });

                count++;
            }
        }
        finally
        {
            await session.CloseAsync();
        }

        return count;
    }

    static async Task VerifyData()
    {
        Console.WriteLine();
        Console.WriteLine("→ Verifying data...");

        var session = _driver!.AsyncSession();
        try
        {
            // Count nodes
            var result = await session.RunAsync(@"
                MATCH (u:AuthUser)
                WITH count(u) as users
                MATCH (p:UserProfile)
                WITH users, count(p) as profiles
                MATCH (e:Exercise)
                WITH users, profiles, count(e) as exercises
                MATCH (c:Club)
                WITH users, profiles, exercises, count(c) as clubs
                MATCH (w:Workout)
                WITH users, profiles, exercises, clubs, count(w) as workouts
                MATCH (po:Post)
                WITH users, profiles, exercises, clubs, workouts, count(po) as posts
                MATCH (co:Comment)
                WITH users, profiles, exercises, clubs, workouts, posts, count(co) as comments
                MATCH (l:Like)
                RETURN users, profiles, exercises, clubs, workouts, posts, comments, count(l) as likes");

            var record = await result.SingleAsync();

            Console.WriteLine($"  • Users: {record["users"].As<int>()}");
            Console.WriteLine($"  • Profiles: {record["profiles"].As<int>()}");
            Console.WriteLine($"  • Exercises: {record["exercises"].As<int>()}");
            Console.WriteLine($"  • Clubs: {record["clubs"].As<int>()}");
            Console.WriteLine($"  • Workouts: {record["workouts"].As<int>()}");
            Console.WriteLine($"  • Posts: {record["posts"].As<int>()}");
            Console.WriteLine($"  • Comments: {record["comments"].As<int>()}");
            Console.WriteLine($"  • Likes: {record["likes"].As<int>()}");

            // Count relationships
            var relResult = await session.RunAsync(@"
                MATCH ()-[r:FOLLOWS]->()
                WITH count(r) as follows
                MATCH ()-[r:TRAINS_AT]->()
                WITH follows, count(r) as trainsAt
                MATCH ()-[r:PERFORMS]->()
                WITH follows, trainsAt, count(r) as performs
                MATCH ()-[r:CONTAINS]->()
                RETURN follows, trainsAt, performs, count(r) as contains");

            var relRecord = await relResult.SingleAsync();

            Console.WriteLine();
            Console.WriteLine("  • FOLLOWS: " + relRecord["follows"].As<int>());
            Console.WriteLine("  • TRAINS_AT: " + relRecord["trainsAt"].As<int>());
            Console.WriteLine("  • PERFORMS: " + relRecord["performs"].As<int>());
            Console.WriteLine("  • CONTAINS: " + relRecord["contains"].As<int>());
        }
        finally
        {
            await session.CloseAsync();
        }
    }
}
