# RepLink API Testing Guide - Postman Collection

Complete guide for testing all RepLink microservices APIs with Postman.

## Base URL

```
http://localhost:8000
```

All requests go through the Ocelot API Gateway.

---

## 1. Authentication Flow

### 1.1 Register User

**POST** `/api/auth/register`

**Headers:**
```
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "email": "athlete@replink.com",
  "username": "fitatlete",
  "password": "Password123!",
  "role": "athlete"
}
```

**Roles:** `athlete`, `coach`, or `influencer`

**Response (200):**
```json
{
  "message": "User registered successfully",
  "userId": "d087778d-fdc9-4509-bb5d-e902026c49e0"
}
```

**Note:** Registration automatically creates a user profile with the username as the display name.

---

### 1.2 Login

**POST** `/api/auth/login`

**Headers:**
```
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "email": "athlete@replink.com",
  "password": "Password123!"
}
```

**Response (200):**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "userId": "d087778d-fdc9-4509-bb5d-e902026c49e0",
  "email": "athlete@replink.com",
  "username": "fitatlete",
  "role": "athlete"
}
```

**⚠️ IMPORTANT:** Copy the `token` value - you'll need it for all authenticated requests!

---

## 2. User Profile Management

### 2.1 Get Current User Profile

**GET** `/api/users/profile`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "id": "profile-id",
  "userId": "user-id",
  "displayName": "Fit Athlete",
  "bio": "Fitness enthusiast",
  "avatarUrl": "https://example.com/avatar.jpg",
  "createdAt": "2025-10-15T10:00:00Z"
}
```

---

### 2.2 Update User Profile

**PUT** `/api/users/profile`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "displayName": "Fit Athlete",
  "bio": "Passionate about fitness and healthy living",
  "avatarUrl": "https://example.com/avatar.jpg"
}
```

**Note:** Profile is automatically created during registration. This endpoint only updates existing profiles.

**Response (200):**
```json
{
  "id": "profile-id",
  "userId": "user-id",
  "displayName": "Fit Athlete",
  "bio": "Passionate about fitness and healthy living",
  "avatarUrl": "https://example.com/avatar.jpg",
  "createdAt": "2025-10-15T10:00:00Z"
}
```

---

### 2.3 Search Users

**GET** `/api/users/search?q=athlete`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
[
  {
    "id": "profile-id",
    "userId": "user-id",
    "displayName": "Fit Athlete",
    "bio": "Fitness enthusiast",
    "avatarUrl": "https://example.com/avatar.jpg",
    "createdAt": "2025-10-15T10:00:00Z"
  }
]
```

---

## 3. Content API (Posts, Comments, Likes)

### 3.1 Create Post

**POST** `/api/posts`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "caption": "Just finished an amazing workout! 💪",
  "mediaUrl": "https://example.com/workout-photo.jpg",
  "hashtags": ["fitness", "workout", "gains"],
  "exerciseIds": []
}
```

**Response (200):**
```json
{
  "id": "post-id",
  "userId": "user-id",
  "caption": "Just finished an amazing workout! 💪",
  "mediaUrl": "https://example.com/workout-photo.jpg",
  "hashtags": ["fitness", "workout", "gains"],
  "exerciseIds": [],
  "createdAt": "2025-10-15T10:00:00Z",
  "commentsCount": 0,
  "likesCount": 0,
  "hasLiked": false
}
```

---

### 3.2 Get All Posts (Feed)

**GET** `/api/posts?limit=20&offset=0`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Query Parameters:**
- `limit` (optional): Number of posts to return (default: 20)
- `offset` (optional): Pagination offset (default: 0)

**Response (200):**
```json
[
  {
    "id": "post-id",
    "userId": "user-id",
    "caption": "Just finished an amazing workout! 💪",
    "mediaUrl": "https://example.com/workout-photo.jpg",
    "hashtags": ["fitness", "workout"],
    "exerciseIds": [],
    "createdAt": "2025-10-15T10:00:00Z",
    "commentsCount": 5,
    "likesCount": 42,
    "hasLiked": true
  }
]
```

---

### 3.3 Get Single Post

**GET** `/api/posts/{postId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "id": "post-id",
  "userId": "user-id",
  "caption": "Just finished an amazing workout! 💪",
  "mediaUrl": "https://example.com/workout-photo.jpg",
  "hashtags": ["fitness", "workout"],
  "exerciseIds": [],
  "createdAt": "2025-10-15T10:00:00Z",
  "commentsCount": 5,
  "likesCount": 42,
  "hasLiked": true
}
```

---

### 3.4 Get User's Posts

**GET** `/api/posts/user/{userId}?limit=20&offset=0`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):** Same as "Get All Posts"

---

### 3.5 Like a Post

**POST** `/api/posts/{postId}/likes`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Body:** None

**Response (200):**
```json
{
  "id": "like-id",
  "postId": "post-id",
  "userId": "user-id",
  "createdAt": "2025-10-15T10:00:00Z"
}
```

---

### 3.6 Unlike a Post

**DELETE** `/api/posts/{postId}/likes`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Like removed"
}
```

---

### 3.7 Get Post Likes Count

**GET** `/api/posts/{postId}/likes`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "postId": "post-id",
  "likesCount": 42
}
```

---

### 3.8 Check if User Liked Post

**GET** `/api/posts/{postId}/likes/me`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "postId": "post-id",
  "hasLiked": true
}
```

---

### 3.9 Add Comment to Post

**POST** `/api/comments`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "postId": "post-id-guid",
  "content": "Great workout! Keep it up! 🔥"
}
```

**Response (200):**
```json
{
  "id": "comment-id",
  "postId": "post-id",
  "userId": "user-id",
  "content": "Great workout! Keep it up! 🔥",
  "createdAt": "2025-10-15T10:00:00Z"
}
```

---

### 3.10 Get Post Comments

**GET** `/api/posts/{postId}/comments`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
[
  {
    "id": "comment-id",
    "postId": "post-id",
    "userId": "user-id",
    "content": "Great workout! Keep it up! 🔥",
    "createdAt": "2025-10-15T10:00:00Z"
  }
]
```

---

### 3.11 Delete Comment

**DELETE** `/api/comments/{commentId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Comment deleted"
}
```

---

## 4. Fitness API (Workouts, Exercises, Clubs)

### 4.1 Create Workout

**POST** `/api/workouts`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "title": "Upper Body Strength",
  "description": "Focus on chest, shoulders, and arms",
  "exerciseIds": ["exercise-id-1", "exercise-id-2"],
  "durationMinutes": 60
}
```

**Response (200):**
```json
{
  "id": "workout-id",
  "userId": "user-id",
  "title": "Upper Body Strength",
  "description": "Focus on chest, shoulders, and arms",
  "exerciseIds": ["exercise-id-1", "exercise-id-2"],
  "durationMinutes": 60,
  "createdAt": "2025-10-15T10:00:00Z"
}
```

---

### 4.2 Get All Workouts

**GET** `/api/workouts?limit=20&offset=0`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
[
  {
    "id": "workout-id",
    "userId": "user-id",
    "title": "Upper Body Strength",
    "description": "Focus on chest, shoulders, and arms",
    "exerciseIds": ["exercise-id-1"],
    "durationMinutes": 60,
    "createdAt": "2025-10-15T10:00:00Z"
  }
]
```

---

### 4.3 Get Single Workout

**GET** `/api/workouts/{workoutId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):** Same as workout object above

---

### 4.4 Get User's Workouts

**GET** `/api/workouts/user/{userId}?limit=20&offset=0`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):** Array of workout objects

---

### 4.5 Update Workout

**PUT** `/api/workouts/{workoutId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "title": "Updated Upper Body Strength",
  "description": "Modified workout plan",
  "exerciseIds": ["exercise-id-1", "exercise-id-2", "exercise-id-3"],
  "durationMinutes": 75
}
```

**Response (200):** Updated workout object

---

### 4.6 Delete Workout

**DELETE** `/api/workouts/{workoutId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Workout deleted successfully"
}
```

---

### 4.7 Create Exercise

**POST** `/api/exercises`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "name": "Bench Press",
  "description": "Classic chest exercise",
  "category": "Strength",
  "mediaUrl": "https://example.com/bench-press.jpg",
  "muscleGroup": "Chest"
}
```

**Response (200):**
```json
{
  "id": "exercise-id",
  "name": "Bench Press",
  "description": "Classic chest exercise",
  "category": "Strength",
  "mediaUrl": "https://example.com/bench-press.jpg",
  "muscleGroup": "Chest",
  "createdAt": "2025-10-15T10:00:00Z"
}
```

---

### 4.8 Get All Exercises (with filters)

**GET** `/api/exercises?category=Strength&muscleGroup=Chest&limit=50&offset=0`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Query Parameters:**
- `category` (optional): Filter by category
- `muscleGroup` (optional): Filter by muscle group
- `limit` (optional): Number of results (default: 50)
- `offset` (optional): Pagination offset (default: 0)

**Response (200):**
```json
[
  {
    "id": "exercise-id",
    "name": "Bench Press",
    "description": "Classic chest exercise",
    "category": "Strength",
    "mediaUrl": "https://example.com/bench-press.jpg",
    "muscleGroup": "Chest",
    "createdAt": "2025-10-15T10:00:00Z"
  }
]
```

---

### 4.9 Get Single Exercise

**GET** `/api/exercises/{exerciseId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):** Exercise object

---

### 4.10 Update Exercise

**PUT** `/api/exercises/{exerciseId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "name": "Barbell Bench Press",
  "description": "Updated description",
  "category": "Strength",
  "mediaUrl": "https://example.com/new-image.jpg",
  "muscleGroup": "Chest"
}
```

**Response (200):** Updated exercise object

---

### 4.11 Delete Exercise

**DELETE** `/api/exercises/{exerciseId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Exercise deleted successfully"
}
```

---

### 4.12 Create Club

**POST** `/api/clubs`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "name": "Iron Warriors Gym",
  "description": "Premier fitness facility",
  "location": "New York, NY"
}
```

**Response (200):**
```json
{
  "id": "club-id",
  "name": "Iron Warriors Gym",
  "description": "Premier fitness facility",
  "location": "New York, NY",
  "createdAt": "2025-10-15T10:00:00Z"
}
```

---

### 4.13 Get All Clubs

**GET** `/api/clubs?limit=50&offset=0`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
[
  {
    "id": "club-id",
    "name": "Iron Warriors Gym",
    "description": "Premier fitness facility",
    "location": "New York, NY",
    "createdAt": "2025-10-15T10:00:00Z"
  }
]
```

---

### 4.14 Get Single Club

**GET** `/api/clubs/{clubId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):** Club object

---

### 4.15 Update Club

**PUT** `/api/clubs/{clubId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "name": "Iron Warriors Fitness Center",
  "description": "Updated description",
  "location": "Manhattan, NY"
}
```

**Response (200):** Updated club object

---

### 4.16 Delete Club

**DELETE** `/api/clubs/{clubId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Club deleted successfully"
}
```

---

### 4.17 Search Clubs

**GET** `/api/clubs/search?query=warriors&limit=20`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):** Array of club objects matching search

---

## 5. Social Graph API (Follow/Unfollow)

### 5.1 Follow User

**POST** `/api/graph/follow/{targetUserId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Followed successfully"
}
```

---

### 5.2 Unfollow User

**POST** `/api/graph/unfollow/{targetUserId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Unfollowed successfully"
}
```

---

### 5.3 Get User's Followers

**GET** `/api/graph/followers/{userId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "userId": "user-id",
  "followers": ["follower-id-1", "follower-id-2"]
}
```

---

### 5.4 Get User's Following

**GET** `/api/graph/following/{userId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "userId": "user-id",
  "following": ["user-id-1", "user-id-2"]
}
```

---

### 5.5 Get Follow Recommendations

**GET** `/api/graph/recommendations`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "recommendations": ["user-id-1", "user-id-2", "user-id-3"]
}
```

---

### 5.6 Get Feed Sources (Users You Follow)

**GET** `/api/graph/feed-sources`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "userIds": ["user-id-1", "user-id-2", "user-id-3"]
}
```

---

### 5.7 User Trains at Club

**POST** `/api/graph/trains-at/{clubId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Training relationship created"
}
```

---

### 5.8 Stop Training at Club

**DELETE** `/api/graph/trains-at/{clubId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "message": "Training relationship removed"
}
```

---

### 5.9 Get User's Clubs

**GET** `/api/graph/user/{userId}/clubs`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "userId": "user-id",
  "clubs": ["club-id-1", "club-id-2"]
}
```

---

### 5.10 Link Workout to Exercises

**POST** `/api/graph/workout/{workoutId}/exercises`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
Content-Type: application/json
```

**Body (JSON):**
```json
{
  "exerciseIds": [
    "exercise-id-1-guid",
    "exercise-id-2-guid",
    "exercise-id-3-guid"
  ]
}
```

**Response (200):**
```json
{
  "message": "Exercises linked to workout"
}
```

---

### 5.11 Get Workout Exercises

**GET** `/api/graph/workout/{workoutId}/exercises`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "workoutId": "workout-id",
  "exercises": ["exercise-id-1", "exercise-id-2"]
}
```

---

## 6. Feed API

### 6.1 Get Personalized Feed

**GET** `/api/feed`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "posts": [
    {
      "id": "post-id",
      "userId": "user-id",
      "caption": "Amazing workout!",
      "mediaUrl": "https://example.com/photo.jpg",
      "hashtags": ["fitness"],
      "exerciseIds": [],
      "createdAt": "2025-10-15T10:00:00Z",
      "commentsCount": 5,
      "likesCount": 42,
      "hasLiked": false
    }
  ]
}
```

**Note:** Returns posts from users you follow. Uses Redis caching (5 min TTL).

---

## 7. Analytics API

### 7.1 Get User Analytics

**GET** `/api/analytics/user/{userId}`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "userId": "user-id-guid",
  "postsCount": 25,
  "workoutsCount": 15,
  "followersCount": 120,
  "followingCount": 80,
  "totalLikes": 500,
  "totalComments": 150,
  "lastUpdated": "2025-10-15T10:00:00Z"
}
```

**Note:** Uses Redis caching (5 min TTL).

---

### 7.2 Get Global Analytics

**GET** `/api/analytics/global`

**Headers:**
```
Authorization: Bearer {YOUR_TOKEN_HERE}
```

**Response (200):**
```json
{
  "totalPosts": 1500,
  "totalWorkouts": 800,
  "totalLikes": 12000,
  "totalComments": 3500,
  "lastUpdated": "2025-10-15T10:00:00Z"
}
```

**Note:** Uses Redis caching (10 min TTL).

---

## 8. Health Check Endpoints (No Auth Required)

### 8.1 Check All Services

**GET** `/api/auth/health`
**GET** `/api/users/health`
**GET** `/api/content/health`
**GET** `/api/fitness/health`
**GET** `/api/graph/health`
**GET** `/api/feed/health`
**GET** `/api/analytics/health`

**Headers:** None required

**Response (200):**
```json
{
  "status": "Healthy",
  "service": "Service Name",
  "database": "Neo4j" or "cache": "Redis - Connected",
  "timestamp": "2025-10-15T10:00:00Z"
}
```

---

## Complete Test Flow Example

### Step 1: Register & Login
1. Register user A (athlete)
2. Register user B (coach)
3. Login as user A → Save token as `TOKEN_A`
4. Login as user B → Save token as `TOKEN_B`

### Step 2: Create Profiles
1. Create profile for user A (use `TOKEN_A`)
2. Create profile for user B (use `TOKEN_B`)

### Step 3: Create Fitness Content
1. User A creates exercise (Bench Press)
2. User A creates workout with exercise
3. User A creates club

### Step 4: Social Interactions
1. User A follows User B
2. User B follows User A back

### Step 5: Post Content
1. User A creates post with workout
2. User B likes User A's post
3. User B comments on User A's post

### Step 6: View Feed
1. User B gets feed → should see User A's post
2. User A gets feed → should see User B's posts (if B posted)

### Step 7: Analytics
1. Get User A analytics → see post/workout counts
2. Get global analytics → see platform totals

---

## Tips for Postman

### Environment Variables
Create a Postman environment with:
- `base_url`: `http://localhost:8000`
- `token`: (copy from login response)
- `userId`: (copy from login response)

### Using Variables in Postman
- URL: `{{base_url}}/api/posts`
- Header: `Authorization: Bearer {{token}}`
- Body: `"userId": "{{userId}}"`

### Save Tokens After Login
In the login request, add to "Tests" tab:
```javascript
var jsonData = pm.response.json();
pm.environment.set("token", jsonData.token);
pm.environment.set("userId", jsonData.userId);
```

---

## Common Error Responses

### 401 Unauthorized
```json
{
  "error": "Unauthorized"
}
```
**Solution:** Check your Bearer token is correct and not expired.

### 400 Bad Request
```json
{
  "error": "Validation error message"
}
```
**Solution:** Check your request body format and required fields.

### 404 Not Found
```json
{
  "error": "Resource not found"
}
```
**Solution:** Verify the ID in the URL exists.

### 500 Internal Server Error
**Solution:** Check service logs with `docker-compose logs -f {service-name}`

---

## Testing Retry Policies

To test Polly retry policies:

1. Stop a service: `docker-compose stop content-api`
2. Try to get feed (Feed API will retry 3 times)
3. Check logs: `docker-compose logs -f feed-api`
4. You'll see retry warnings in logs
5. Restart service: `docker-compose start content-api`

---

## Notes

- All timestamps are in UTC ISO 8601 format
- All IDs are GUIDs (format: `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`)
- Token expires after 7 days
- Rate limiting: Not implemented (but retry policy handles 429 responses)
- Pagination: Use `limit` and `offset` query parameters where available

---

**Happy Testing! 🚀**
