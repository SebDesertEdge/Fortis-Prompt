I will describe in this document my steps to build the solution

## Steps
1. I started for discribing evrything to Claude Code as described in [ClaudeApproach README](docs/ClaudeApproach/README.md)
2. When I reviewed the implemented solution from Claude I notices that for sure some Unity methods called outside the 
main thread will trigger errors in the implementation, as Claude didn't take in consideration the Unity primarily 
single-threaded for its core logic
3. Having that in consideration I decided to implement a Dependency injection [monobehaviour installer](Assets/code/ClaudeApproach/ClaudeApproach/Installers/MonoInstaller.cs)
to be able to test the Claude solution and help me with the project running.
4. I created a IAnalyticsService interface to be able to mock the analytics service in the tests. Created a new 
AnalyticsService with my own logic and implemented the interface in the Claude solution.
5. I worked on the implementation of [ResilientAnalytics.cs] (Assets/Code/ResilientAnalytics.cs), my own implementation of 
the AnalyticsService, in base of what claude give me and my own understanding of the problem. I have this considerations:
   - Needed to find a way to 

