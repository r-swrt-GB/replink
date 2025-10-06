import { useAuth } from '../context/AuthContext';
import { useNavigate } from 'react-router-dom';

const Dashboard = () => {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  const handleLogout = () => {
    logout();
    navigate('/login');
  };

  return (
    <div className="min-h-screen bg-gradient-to-br from-slate-900 via-slate-800 to-orange-900">
      {/* Header */}
      <nav className="bg-slate-800/50 backdrop-blur-lg border-b border-slate-700">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="flex justify-between items-center h-16">
            <h1 className="text-2xl font-bold text-white">
              Rep<span className="text-orange-500">Link</span>
            </h1>
            <button
              onClick={handleLogout}
              className="px-4 py-2 bg-slate-700 hover:bg-slate-600 text-white rounded-lg transition"
            >
              Logout
            </button>
          </div>
        </div>
      </nav>

      {/* Content */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-12">
        <div className="bg-slate-800/50 backdrop-blur-lg rounded-2xl shadow-2xl p-8 border border-slate-700">
          <h2 className="text-3xl font-bold text-white mb-4">Welcome to RepLink! 🏋️</h2>
          <p className="text-slate-300 mb-6">
            You're successfully logged in as <span className="text-orange-500 font-semibold">{user?.email}</span>
          </p>

          <div className="bg-gradient-to-r from-orange-500/10 to-orange-600/10 border border-orange-500/30 rounded-xl p-6">
            <h3 className="text-xl font-semibold text-white mb-2">Dashboard Coming Soon</h3>
            <p className="text-slate-300">
              We're building an amazing fitness social experience. Stay tuned for:
            </p>
            <ul className="mt-4 space-y-2 text-slate-300">
              <li className="flex items-center">
                <span className="text-orange-500 mr-2">✓</span>
                Post your workout achievements
              </li>
              <li className="flex items-center">
                <span className="text-orange-500 mr-2">✓</span>
                Follow other athletes
              </li>
              <li className="flex items-center">
                <span className="text-orange-500 mr-2">✓</span>
                Personalized fitness feed
              </li>
              <li className="flex items-center">
                <span className="text-orange-500 mr-2">✓</span>
                Track your progress
              </li>
            </ul>
          </div>
        </div>
      </div>
    </div>
  );
};

export default Dashboard;
